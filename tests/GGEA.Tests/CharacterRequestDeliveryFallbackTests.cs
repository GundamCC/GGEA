using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;
using System.IO;

namespace GGEA.Tests;

public partial class CharacterRequestPageTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true, true)]
    public async Task Delivery_ReopensOriginalRoleAndOnlyRetriesInventoryOnce(bool made, bool eligible, bool obtain = false, bool firstDevelopmentRequired = false)
    {
        void Fix(ScreenImage f, string name, int x, int y) =>
            Paste(f, Path.Combine(AppContext.BaseDirectory, "Fixtures/Base/delivery_" + name + ".png"), x, y);
        var cards = CreateList(true, false, true);
        void Complete(int slot)
        {
            for (int y = 604; y < 678; y++) Array.Clear(cards.Bgra, (y * 1920 + 191 + 535 * slot) * 4, 166 * 4);
            PasteTemplate(cards, "card_refresh.png", 272 + 535 * slot, 644);
        }
        if (!firstDevelopmentRequired) Complete(1);
        var list = Encode(cards);
        Complete(0);
        var finished = Encode(cards);

        var frame = Empty();
        PasteTemplate(frame, "title_character_requests.png", 216, 17);
        PasteTemplate(frame, "button_next_character.png", 1869, 502);
        PasteTemplate(frame, "title_deliver_unit.png", 1403, 246);
        PasteTemplate(frame, "button_deliver.png", 1584, 837);
        Paste(frame, ConfigService.ResolvePath("resources/templates/common/back.png"), 40, 14);
        Fix(frame, "number1", 1538, 243);
        Fix(frame, "scirocco_portrait", 544, 467);
        var detail = Encode(frame);
        for (int y = 800; y < 910; y++) Array.Clear(frame.Bgra, (y * 1920 + 1540) * 4, 170 * 4);
        PasteTemplate(frame, "button_obtain.png", 1554, 839);
        var unavailable = Encode(frame);
        frame = Empty();
        PasteTemplate(frame, "title_confirm_delivery.png", 874, 71);
        PasteTemplate(frame, "button_cancel_delivery.png", 729, 967);
        Fix(frame, "target", 503, 286);
        Fix(frame, "empty_count", 1337, 799);
        Fix(frame, "amount_one", 1000, 343);
        var empty = Encode(frame);
        Fix(frame, "bottom_row", 490, 600);
        var available = Encode(frame);
        Fix(frame, "selected_count", 1337, 799);
        PasteTemplate(frame, "delivery_selected_one.png", 605, 610);
        PasteTemplate(frame, "button_submit_delivery.png", 1103, 967);
        var selected = Encode(frame);
        frame = Empty();
        PasteTemplate(frame, "title_delivery_complete.png", 817, 337);
        var complete = Encode(frame);
        frame = Empty();
        PasteTemplate(frame, "title_claim_result.png", 868, 60);
        var ok = ConfigService.ResolvePath("resources/templates/dialog/button_ok.png");
        var image = ImageCodec.DecodePng(File.ReadAllBytes(ok));
        Paste(frame, ok, 960 - image.Width / 2, 995 - image.Height / 2);

        var steps = obtain
            ? new List<byte[]> { list, unavailable, made && eligible ? detail : unavailable }
            : new List<byte[]> { list, detail, empty, detail, list, detail };
        if (made && (!obtain || eligible))
        {
            steps.Add(eligible ? available : empty);
            if (eligible) steps.AddRange([selected, complete, Encode(frame)]);
            else steps.Add(detail);
        }
        var adb = new FakeAdbService(list);
        adb.ScreenshotProvider = () => adb.Taps.Count < steps.Count ? steps[adb.Taps.Count] : eligible ? finished : list;
        var log = new TestLog();
        using var ocr = new OnnxTextRecognizer();
        using var auto = new AutomationService(adb, new TemplateMatcher(), log);
        int calls = 0;
        using var service = new CharacterRequestService(auto, log, ocr)
        {
            RestockFromMethods = (_, offer, _, _) =>
            {
                Assert.True(obtain);
                Assert.Equal("1080001600", offer.UnitId);
                calls++;
                return Task.FromResult(firstDevelopmentRequired ? ObtainResult.FirstDevelopmentRequired :
                    made ? ObtainResult.Produced : ObtainResult.Unavailable);
            },
            RestockFromList = (_, target, _, _) =>
            {
                Assert.False(obtain);
                calls++;
                Assert.Equal(("1080001600", "810", 2, 2000), (target.UnitId, target.Route.SeriesId, target.Route.Tab, target.Route.Capital));
                return Task.FromResult(made);
            }
        };
        var result = await service.RunFromListAsync("test", default);
        if (obtain)
            Assert.InRange(adb.Taps[1].X, 1554, 1691);
        if (firstDevelopmentRequired) Assert.Contains("首次开发", Assert.Single(result.FailedRoles));
        Assert.Equal(1, calls);
        Assert.Equal(eligible ? 1 : 0, result.Reported);
        Assert.Equal(eligible ? 0 : 1, result.FailedRoles.Count);
        Assert.Equal(steps.Count, adb.Taps.Count);
    }

    [Fact]
    public async Task Delivery_ShortFilteredListTapsInsideCardAndConfirmsTheSameSelection()
    {
        var frame = Empty();
        void Fix(string name, int x, int y) => Paste(frame,
            Path.Combine(AppContext.BaseDirectory, "Fixtures/Base/delivery_" + name + ".png"), x, y);
        PasteTemplate(frame, "title_confirm_delivery.png", 874, 71);
        PasteTemplate(frame, "button_cancel_delivery.png", 729, 967);
        Fix("empty_count", 1337, 799);
        Fix("amount_one", 1000, 343);
        Fix("short_row", 490, 501);
        using var ocr = new OnnxTextRecognizer();
        var page = new DeliveryPage(ocr, new(), 35);
        var card = Assert.IsType<DeliveryPage.Card>(page.Read(frame, default)?.Candidate);
        Assert.Equal((580, 35, 0), (card.X, card.Level, card.LimitBreaks));
        var available = Encode(frame);
        Fix("selected_count", 1337, 799);
        PasteTemplate(frame, "delivery_selected_one.png", 605, 514);
        PasteTemplate(frame, "button_submit_delivery.png", 1103, 967);
        var selected = Encode(frame);
        var adb = new FakeAdbService(available);
        adb.ScreenshotProvider = () => adb.Taps.Count == 1 && adb.Taps[0].X == 580 &&
            adb.Taps[0].Y is >= 530 and <= 635 ? selected : available;
        using var auto = new AutomationService(adb, new TemplateMatcher(), new TestLog());
        Assert.True(await new UnitDeliveryService(auto, ocr, new(), 35).SubmitAsync("test", default));
        Assert.Equal(2, adb.Taps.Count);
        Assert.Empty(adb.Swipes);

        // 仅剩带锁卡时，即使关闭等级和突破保护，也不能选中该卡。
        frame = ImageCodec.DecodePng(available);
        for (var y = 501; y < 687; y++)
            Array.Clear(frame.Bgra, (y * 1920 + 490) * 4, 380 * 4);
        var locked = new DeliveryPage(ocr, new(false, false), 1).Read(frame, default);
        Assert.NotNull(locked);
        Assert.Null(locked.Candidate);
        Assert.False(locked.UncertainCards);

        // 可见卡的等级读不清时，禁止取消库存并把它当作无合格单位。
        frame = ImageCodec.DecodePng(available);
        for (var y = 620; y < 655; y++)
            for (var x = 580; x <= 770; x += 190)
                Array.Clear(frame.Bgra, (y * 1920 + x - 39) * 4, 78 * 4);
        var unreadable = new FakeAdbService(Encode(frame));
        using var guarded = new AutomationService(unreadable, new TemplateMatcher(), new TestLog());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new UnitDeliveryService(guarded, ocr, new(), 35).SubmitAsync("test", default));
        Assert.Empty(unreadable.Taps);
    }

    [Fact]
    public void StrengthBaselineUsesOfferSeriesEvenWithoutDeliveryRoute()
    {
        var offer = new StrengthOffer("1121900010101", "新机动战记高达W 无尽的华尔兹", 1);
        var request = new StrengthRequest("1219000101", "希罗·尤尔 (EW版)", offer, 0);
        var target = Assert.IsType<DevelopmentTarget>(DevelopmentRequestIndex.ForStrength(request));
        Assert.Equal(("1211000300", "2190", "121901", 1, 4000),
            (target.UnitId, target.Route.SeriesId, target.Route.DiagramId, target.Route.Tab, target.Route.Capital));
        Assert.Null(target.Route.Level);
        Assert.Null(DevelopmentRequestIndex.ForStrength(request with { RoleId = "1210000100" }));
    }

    [Fact]
    public void Delivery_ValidatesCostLevelAndDuplicateProduction()
    {
        var v = new DeliveryOffer("1175000100", "V高达", 1) { RoleId = "1175000100", Number = 5 };
        var route = Assert.IsType<DevelopmentTarget>(DevelopmentRequestIndex.ForDelivery(v, new())).Route;
        Assert.Equal(("1750", 55, 27000), (route.SeriesId, route.Level, route.Capital));
        Assert.Null(DevelopmentRequestIndex.ForDelivery(v with { MinimumLevel = 56 }, new()));
        Assert.Null(DevelopmentRequestIndex.ForDelivery(v with { UnitId = "0" }, new()));
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var period = RequestProductionAttempt.RefreshPeriod;
            Assert.True(new RequestProductionAttempt("device", 1, v, period, dir.FullName).Reserve());
            Assert.False(new RequestProductionAttempt("device", 1, v, period, dir.FullName).Reserve());
            Assert.False(new RequestProductionAttempt("device", 0, v, period - 1, dir.FullName).Reserve());
        }
        finally { dir.Delete(true); }
    }
}
