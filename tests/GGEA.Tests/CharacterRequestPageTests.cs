using GGEA.App.Config;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.Vision;
using System.IO;

namespace GGEA.Tests;

public partial class CharacterRequestPageTests
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
    public async Task AcceptFromListAsync_AdvancesWithoutDialogueMarkersAndStopsOnChallenge()
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
        var transition = Encode(Empty());
        var adb = new FakeAdbService(list);
        adb.ScreenshotProvider = () => adb.Taps.Count switch
        {
            1 => accept,
            2 or 3 => transition,
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Funding_CompletesWithoutPortrait(bool alreadyAccepted, bool weekly)
    {
        // 所有详情的立绘区均为空。只有调度资金标题与相符的 CAPITAL 数量可以触发交付。
        var before = CreateList(true, false, true);
        void CompleteSlot(int slot)
        {
            int dx = 535 * slot;
            for (int y = 604; y < 678; y++) Array.Clear(before.Bgra, (y * 1920 + 191 + dx) * 4, 166 * 4);
            PasteTemplate(before, "card_refresh.png", 272 + dx, 644);
        }
        CompleteSlot(1);
        var list = Encode(before);
        CompleteSlot(0);
        var finished = Encode(before);

        var detail = Empty();
        PasteTemplate(detail, "title_character_requests.png", 216, 17);
        PasteTemplate(detail, "button_next_character.png", 1869, 502);
        PasteTemplate(detail, "title_funding.png", 1403, 246);
        Paste(detail, ConfigService.ResolvePath("resources/templates/common/back.png"), 40, 14);
        PasteTemplate(detail, "button_accept.png", 1442, 798);
        var accept = Encode(detail);
        PasteTemplate(detail, "button_deliver.png", 1584, 837);
        Paste(detail, Path.Combine(AppContext.BaseDirectory, "Fixtures/Base/funding_progress.png"), 1680, 404);
        var deliver = Encode(detail);

        var dialog = Empty();
        PasteTemplate(dialog, "title_confirm_delivery.png", 874, 216);
        PasteTemplate(dialog, "button_submit_delivery.png", 1103, 822);
        using var ocr = new OnnxTextRecognizer();
        var funding = new FundingRequest(ocr);
        Assert.Null(funding.ReadConfirmation(dialog, 5000, default));
        Paste(dialog, Path.Combine(AppContext.BaseDirectory, "Fixtures/Base/funding_cost.png"), 503, 541);
        Assert.Null(funding.ReadConfirmation(dialog, 1000, default));
        Assert.Equal(5000, funding.ReadAmount(detail, default));
        var confirm = Encode(dialog);

        var complete = Empty();
        PasteTemplate(complete, "title_delivery_complete.png", 817, 337);
        var reward = Empty();
        PasteTemplate(reward, "title_claim_result.png", 868, 60);
        var ok = ConfigService.ResolvePath("resources/templates/dialog/button_ok.png");
        var okImage = ImageCodec.DecodePng(File.ReadAllBytes(ok));
        Paste(reward, ok, 960 - okImage.Width / 2, 995 - okImage.Height / 2);
        var milestone = Empty();
        PasteTemplate(milestone, "title_weekly_reward.png", 821, 349);
        PasteTemplate(milestone, "weekly_tap_to_next.png", 823, 932);

        var steps = new List<(byte[] Screen, (int, int) Tap)> { (list, (427, 410)) };
        if (!alreadyAccepted)
        {
            steps.Add((accept, (1623, 857)));
            steps.Add((list, (427, 410))); // 承接后回到列表时，重新打开同一栏并核对标题。
        }
        steps.Add((deliver, (1623, 857)));
        steps.Add((confirm, (1151, 848)));
        // 两帧没有台词控件的过场与后续预告都只需点中央，即使已领取奖励也应继续。
        var interlude = Encode(Empty());
        steps.Add((interlude, (960, 540)));
        steps.Add((Encode(complete), (960, 540)));
        steps.Add((Encode(reward), (960, 995)));
        if (weekly) steps.Add((Encode(milestone), (960, 950)));
        steps.Add((interlude, (960, 540)));
        var adb = new FakeAdbService(list);
        adb.ScreenshotProvider = () => adb.Taps.Count < steps.Count ? steps[adb.Taps.Count].Screen : finished;
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var result = await new CharacterRequestService(automation, log, ocr).RunFromListAsync("serial");
        Assert.Equal(alreadyAccepted ? 0 : 1, result.Accepted);
        Assert.Equal(1, result.Reported);
        Assert.Empty(result.FailedRoles);
        Assert.Equal(steps.Select(step => step.Tap), adb.Taps);
    }

    [Theory]
    [InlineData("development_zero.png", 0)]
    [InlineData("development_three.png", 3)]
    public void Development_UsesRoleAndOfferNumberAndVerifiesProgress(string counter, int progress)
    {
        var screen = Empty();
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures/Base");
        var title = new Roi(1403, 246, 135, 35);
        Paste(screen, Path.Combine(fixtures, "development_number6.png"), 1538, 243);
        Paste(screen, Path.Combine(fixtures, counter), 1703, 410);
        using var ocr = new OnnxTextRecognizer();
        var catalog = new CharacterRequestCatalog(ocr);
        Assert.Null(catalog.ReadDevelopment(screen, title, default));
        Paste(screen, Path.Combine(fixtures, "develop_shakti_detail.png"), 473, 304);
        var order = Assert.IsType<DevelopmentOrder>(catalog.ReadDevelopment(screen, title, default));
        Assert.Equal(("1750", "1175000400", 4000, 3, progress),
            (order.Offer.SeriesId, order.Offer.UnitId, order.Offer.Capital, order.Offer.Amount, order.Progress));
        Assert.Equal("1001003800", DevelopmentRequestIndex.Find("1001001801", 6)?.UnitId);
        Assert.Null(DevelopmentRequestIndex.Find("1001001801", 9));
        Paste(screen, Path.Combine(fixtures, counter), 1703, 458);
        Assert.Null(catalog.ReadDevelopment(screen, title, default));
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
