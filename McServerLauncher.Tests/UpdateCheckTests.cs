using System.Reflection;
using McServerLauncher.Localization;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// What "Check for updates" says when it could not check.
/// </summary>
/// <remarks>
/// <para>
/// Found looking for relatives of the upper-case hash bug. That one hid for as long as the feature
/// existed because its only symptom was good news — "everything is up to date" — and nobody reports
/// good news. The same message was also the answer to having no connection at all: the store call
/// swallowed the error, returned an empty map, and an empty map reads as "no mod has a newer
/// version". The comment in the code said so, as a decision.
/// </para>
/// <para>
/// So the contract is pinned twice: the lookup has to be able to say "I could not ask", and the
/// line under the list has to say it too.
/// </para>
/// </remarks>
public class UpdateCheckTests
{
    [Fact]
    public void OfflineIsNeverReportedAsUpToDate()
    {
        var text = ServerModsViewModel.UpdateStatusText(couldAsk: false, updates: 0);

        Assert.NotEqual(Localizer.Get("Msg_NoUpdates"), text);
        Assert.Equal(Localizer.Get("Msg_UpdatesCheckFailed"), text);
    }

    [Fact]
    public void UpToDateOnlyWhenTheStoreSaidSo()
    {
        Assert.Equal(Localizer.Get("Msg_NoUpdates"),
            ServerModsViewModel.UpdateStatusText(couldAsk: true, updates: 0));

        var found = ServerModsViewModel.UpdateStatusText(couldAsk: true, updates: 3);
        Assert.Contains("3", found);
        Assert.DoesNotContain("{0}", found);
    }

    [Fact]
    public void TheUpdateLookupCanSayItCouldNotAsk()
    {
        // The whole fix rests on this return type. An empty map is what "every mod is on its newest
        // version" looks like, so if the lookup ever goes back to returning one on failure, the tab
        // goes back to reporting good news with no connection — and nothing else would notice.
        var method = typeof(ModrinthService).GetMethod(nameof(ModrinthService.GetLatestVersionsByHashAsync))!;
        var returned = new NullabilityInfoContext().Create(method.ReturnParameter);

        var dictionary = Assert.Single(returned.GenericTypeArguments);
        Assert.Equal(NullabilityState.Nullable, dictionary.ReadState);
    }
}
