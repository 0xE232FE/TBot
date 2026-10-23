using System.Reflection;
using Tbot.Includes;
using Tbot.Services;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Tests;

public class RunningProfilesTests {
    public static IEnumerable<object[]> Profiles() {
        yield return new object[] { null, "No profile is currently running." };
        yield return new object[] { Array.Empty<string>(), "No profile is currently running." };
        yield return new object[] { new[] { "mining" }, "Profile currently running:\n- mining\n" };
        yield return new object[] { new[] { "mining", "expeditions" }, "Profiles currently running:\n- mining\n- expeditions\n" };
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public async Task Listing_running_profiles_sends_the_expected_message(string[] profiles, string expectedText) {
        var messages = new List<string>();
        var ogame = StrictInterfaceProxy.Make<IOgameService>((method, args) => method.Name switch {
            "add_OnError" => null,
            _ => throw new NotSupportedException(method.Name)
        });
        var scheduler = StrictInterfaceProxy.Make<IFleetScheduler>((method, args) => method.Name switch {
            "SetTBotInstance" or "SetTBotOgameBridge" => null,
            _ => throw new NotSupportedException(method.Name)
        });
        var messenger = StrictInterfaceProxy.Make<ITelegramMessenger>((method, args) => {
            if (method.Name != "SendMessage" || args[0] is not string message)
                throw new NotSupportedException(method.Name);
            messages.Add(message);
            return Task.CompletedTask;
        });
        var bot = new TBotMain(ogame, scheduler, null, null, null);
        // A freshly constructed bot leaves runningProfiles null until profiles are loaded.
        if (profiles != null)
            bot.userData.runningProfiles = profiles.ToList();
        bot.userData.userInfo = new UserInfo { PlayerName = "Commander" };
        bot.userData.serverData = new ServerData { Name = "Universe" };
        typeof(TBotMain).GetField("telegramMessenger", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(bot, messenger);

        await bot.ListRunningProfiles();

        Assert.Equal("<code>[Commander@Universe]</code>\n" + expectedText, Assert.Single(messages));
    }
}
