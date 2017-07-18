using System.Web;
using Sitecore.Analytics;
using Sitecore.ContentTesting.Configuration;
using Sitecore.ContentTesting.Data;
using Sitecore.ContentTesting.Inspectors;
using Sitecore.ContentTesting.Model.Data.Items;
using Sitecore.ContentTesting.Models;
using Sitecore.ContentTesting.Pipelines.DetermineTestExposure;
using Sitecore.ContentTesting.Pipelines.GetCurrentTestCombination;
using Sitecore.ContentTesting.Pipelines.SuspendTest;
using Sitecore.Data;
using Sitecore.Data.Items;
using Sitecore.Diagnostics;
using Sitecore.ContentTesting;
using Sitecore.Mvc.Pipelines.Request.RequestBegin;
using Sitecore.ContentTesting.Pipelines;

namespace Sitecore.Support.ContentTesting.Mvc.Pipelines.Response.RequestBegin
{
  public class EvaluateTestExposure : EvaluateTestExposureBase<RequestBeginArgs>
  {/// <summary>
   /// Initializes a new instance of the <see cref="EvaluateTestExposure"/> class.
   /// </summary>
    public EvaluateTestExposure()
      : base(null, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EvaluateTestExposure"/> class.
    /// </summary>
    /// <param name="contentTestStore">The <see cref="IContentTestStore"/> to read test data from.</param>
    /// <param name="factory">The <see cref="IContentTestingFactory"/> used to load types.</param>
    public EvaluateTestExposure([CanBeNull] IContentTestStore contentTestStore, [CanBeNull] IContentTestingFactory factory)
      : base(contentTestStore, factory)
    {
    }

    /// <summary>
    /// Gets the item being requested so be processed.
    /// </summary>
    /// <param name="args">The arguments being processed.</param>
    /// <returns>The item that was requested.</returns>
    protected override Item GetRequestItem(RequestBeginArgs args)
    {
      return Context.Item;
    }

    /// <summary>
    /// Process this processor
    /// </summary>
    /// <param name="args">The arguments for the request</param>
    public new void Process(RequestBeginArgs args)
    {
      Assert.ArgumentNotNull(args, "args");

      // Check if content testing is enabled
      if (!Settings.IsAutomaticContentTestingEnabled)
      {
        return;
      }

      // Check if we're in the shell. No testing in the shell
      if (Context.Site != null && Context.Site.Name == Sitecore.Constants.ShellSiteName)
      {
        return;
      }

      var item = GetRequestItem(args);

      // Context item may be null in the shell. Don't assert for it
      if (item == null)
      {
        return;
      }

      // Check if there are any running tests on the page
      var testConfiguration = FindTestForItem(item, Context.Device.ID);
      if (testConfiguration == null || !testConfiguration.TestDefinitionItem.IsRunning)
      {
        return;
      }

      var testCombinationContext = factory.GetTestCombinationContext(new HttpContextWrapper(HttpContext.Current));

      var testSet = TestManager.GetTestSet(new[] { testConfiguration.TestDefinitionItem }, item, Context.Device.ID);

      // Check if a combination is being forced. Used for screenshots when a test is already running (test results gallery)
      if (factory.EditModeContext.TestCombination != null)
      {
        var combination = new TestCombination(factory.EditModeContext.TestCombination, testSet);

        if (!ValidateCombinationDatasource(combination, testConfiguration))
        {
          factory.TestingTracker.ClearMvTest();
          testCombinationContext.SaveToResponse(testSet.Id, null);

          return;
        }

        factory.TestingTracker.SetTestCombination(combination, testConfiguration.TestDefinitionItem, false);

        return;
      }

      // If no forced combination and we're editing, jump out. We don't want to test in page editor
      if (Context.PageMode.IsPageEditor)
      {
        return;
      }

      // Bots cause tracker to be inactive. Don't test on bots
      if (Tracker.Current == null || !Tracker.IsActive)
      {
        return;
      }

      if (testCombinationContext.IsSetInRequest())
      {
        var values = testCombinationContext.GetFromRequest(testSet.Id);
        if (values != null)
        {
          // ensure the values from request are the same size as the test set. The cookie may have been old and the test may have changed
          if (values.Length == testSet.Variables.Count)
          {
            // ensure each value from the request is still valid
            var valid = true;
            for (int i = 0; i < values.Length; i++)
            {
              valid = valid && (values[i] <= testSet.Variables[i].Values.Count - 1);
            }

            if (valid)
            {
              var combination = new TestCombination(values, testSet);

              if (!ValidateCombinationDatasource(combination, testConfiguration))
              {
                factory.TestingTracker.ClearMvTest();
                testCombinationContext.SaveToResponse(testSet.Id, null);

                return;
              }

              factory.TestingTracker.SetTestCombination(combination, testConfiguration.TestDefinitionItem, false);
              return;
            }
          }
        }
      }

      var shouldExpose = ShouldIncludeRequestByTrafficAllocation(item, testConfiguration);

      if (shouldExpose)
      {
        var selectExperienceArgs =
          new GetCurrentTestCombinationArgs(new TestDefinitionItem[] { testConfiguration.TestDefinitionItem })
          {
            Item = item,
            DeviceID = Context.Device.ID
          };

        GetCurrentTestCombinationPipeline.Instance.Run(selectExperienceArgs);

        if (selectExperienceArgs.Combination != null)
        {
          if (!ValidateCombinationDatasource(selectExperienceArgs.Combination, testConfiguration))
          {
            factory.TestingTracker.ClearMvTest();
            testCombinationContext.SaveToResponse(testSet.Id, null);

            return;
          }

          factory.TestingTracker.SetTestCombination(selectExperienceArgs.Combination, testConfiguration.TestDefinitionItem);
          testCombinationContext.SaveToResponse(selectExperienceArgs.Combination.Testset.Id, selectExperienceArgs.Combination.Combination);
        }
      }
      else
      {
        testCombinationContext.SaveToResponse(testSet.Id, null);
      }
    }

    /// <summary>
    /// Validates the combination datasource.
    /// </summary>
    /// <param name="combination">The combination.</param>
    /// <param name="testConfiguration">The test configuration.</param>
    /// <returns></returns>
    private bool ValidateCombinationDatasource(TestCombination combination, ITestConfiguration testConfiguration)
    {
      // Check that the combination configuration is not broken
      var testValueInspector = new TestValueInspector();

      for (int i = 0; i < combination.Combination.Length; i++)
      {
        var testValue = combination.GetValue(i);

        if (!testValueInspector.IsValidDataSource(testConfiguration.TestDefinitionItem, testValue))
        {
          var suspendTestArgs = new SuspendTestArgs(testConfiguration);
          SuspendTestPipeline.Instance.Run(suspendTestArgs);

          return false;
        }
      }

      return true;
    }
  }
}