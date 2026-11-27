using GGEA.App.Config;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.Vision;
using System.IO;

namespace GGEA.Tests;

public class CharacterRequestPageTests
{
    // 第三栏锁块来自 MuMu 的原始列表截图，独立于生产代码放大的历史 BOSS 模板。
    [Theory]
    [InlineData(true, false, true, 2)]
    [InlineData(false, true, true, 3)]
    [InlineData(false, false, true, null)]
    [InlineData(true, true, true, null)]
    [InlineData(true, false, false, null)]
    public void SlotCount_RequiresPageAndUnambiguousCards(bool locked, bool thirdRank, bool hasTitle, int? expected)
    {
        var screen = CreateList(locked, thirdRank, hasTitle);
        Assert.Equal(expected, new CharacterRequestPage().ReadSlotCount(screen, default));
    }

    // 初次锁定后即使后续回放显示第三张角色卡，也不扩大已确定的本轮上限。
    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    public async Task AcceptFromListAsync_OnlyTapsRolesWithinInitialLimit(bool locked, int expected)
    {
        var list = Encode(CreateList(locked, !locked, true));
        var three = Encode(CreateList(false, true, true));
        var detail = Empty();
        PasteTemplate(detail, "title_character_requests.png", 216, 17);
        PasteTemplate(detail, "button_next_character.png", 1869, 502);
        PasteTemplate(detail, "button_accept.png", 1442, 798);
        Paste(detail, ConfigService.ResolvePath("resources/templates/common/back.png"), 40, 14);
        var detailPng = Encode(detail);
        var adb = new FakeAdbService(list);
        adb.ScreenshotProvider = () => adb.Taps.Count == 0 ? list :
            adb.Taps.Count % 2 == 1 ? detailPng : three;
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var service = new CharacterRequestService(automation, log);
        Assert.Equal(expected, await service.AcceptFromListAsync("serial"));
        var taps = Enumerable.Range(0, expected)
            .SelectMany(i => new[] { (427 + i * 535, 410), (1623, 857) });
        Assert.Equal(taps, adb.Taps);
    }

    [Fact]
    public async Task AcceptFromListAsync_AdvancesOnlyConfirmedDialogueAndLeavesChallengeUntouched()
    {
        var list = Encode(CreateList(true, false, true));
        var detail = Empty();
        PasteTemplate(detail, "title_character_requests.png", 216, 17);
        PasteTemplate(detail, "button_next_character.png", 1869, 502);
        Paste(detail, ConfigService.ResolvePath("resources/templates/common/back.png"), 40, 14);
        PasteTemplate(detail, "button_accept.png", 1442, 798);
        var accept = Encode(detail);
        PasteTemplate(detail, "button_challenge.png", 1584, 837);
        var challenge = Encode(detail);
        var dialogue = Empty();
        PasteTemplate(dialogue, "dialog_com_frame.png", 262, 145);
        var incomplete = Encode(dialogue);
        PasteTemplate(dialogue, "dialog_advance.png", 741, 836);
        var first = Encode(dialogue);
        dialogue = Empty();
        PasteTemplate(dialogue, "dialog_com_frame.png", 262, 145);
        PasteTemplate(dialogue, "dialog_advance.png", 779, 839);
        var second = Encode(dialogue);

        var adb = new FakeAdbService(list);
        var pendingFrames = 0;
        adb.ScreenshotProvider = () => adb.Taps.Count switch
        {
            1 => accept,
            2 when pendingFrames++ < 2 => incomplete,
            2 => first,
            3 => second,
            4 or 6 => challenge,
            _ => list
        };
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log);
        Assert.Equal(1, await new CharacterRequestService(automation, log).AcceptFromListAsync("serial"));
        var back = ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath("resources/templates/common/back.png")));
        var backPoint = (40 + back.Width / 2, 14 + back.Height / 2);
        Assert.Equal(new[] { (427, 410), (1623, 857), (960, 540), (960, 540), backPoint,
            (962, 410), backPoint }, adb.Taps);
    }

    [Fact]
    public void ReportedSlot_RemainsUnlockedAndOnlyNextRoleIsActive()
    {
        var frame = CreateList(true, false, true);
        for (var y = 604; y < 678; y++)
            Array.Clear(frame.Bgra, (y * 1920 + 191) * 4, 166 * 4);
        PasteTemplate(frame, "card_refresh.png", 272, 644);
        Assert.Equal(new CharacterRequestPage.ListState(2, 2), new CharacterRequestPage().ReadList(frame, default));
    }

    private const string Root = "resources/templates/base/character_requests/";
    private static byte[] Encode(ScreenImage image) => ImageCodec.EncodePng(image.Bgra, image.Width, image.Height);
    private static ScreenImage Empty() => new()
        { Width = 1920, Height = 1080, Bgra = TestImageFactory.CreateBgra(1920, 1080, 30) };

    private static ScreenImage CreateList(bool locked, bool thirdRank, bool hasTitle)
    {
        var screen = Empty();
        if (hasTitle) PasteTemplate(screen, "title_character_requests.png", 216, 17);
        for (var slot = 0; slot < (thirdRank ? 3 : 2); slot++)
            PasteTemplate(screen, "card_request_rank.png", 205 + slot * 535, 621);
        if (locked) Paste(screen, Path.Combine(AppContext.BaseDirectory, "Fixtures/Base/character_third_slot_lock.png"), 1452, 284);
        return screen;
    }

    private static void PasteTemplate(ScreenImage screen, string name, int x, int y) =>
        Paste(screen, ConfigService.ResolvePath(Root + name), x, y);

    private static void Paste(ScreenImage screen, string path, int x, int y)
    {
        var image = ImageCodec.DecodePng(File.ReadAllBytes(path));
        for (var row = 0; row < image.Height; row++)
            Buffer.BlockCopy(image.Bgra, row * image.Width * 4, screen.Bgra,
                ((y + row) * screen.Width + x) * 4, image.Width * 4);
    }
}
