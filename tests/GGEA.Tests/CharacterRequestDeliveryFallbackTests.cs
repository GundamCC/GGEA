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
    public async Task Delivery_ReopensOriginalRoleAndOnlyRetriesInventoryOnce(bool made, bool eligible)
    {
        void Fix(ScreenImage f, string name, int x, int y) =>
            Paste(f, Path.Combine(AppContext.BaseDirectory, "Fixtures/Base/delivery_" + name + ".png"), x, y);
        var cards = CreateList(true, false, true);
        void Complete(int slot)
        {
            for (int y = 604; y < 678; y++) Array.Clear(cards.Bgra, (y * 1920 + 191 + 535 * slot) * 4, 166 * 4);
            PasteTemplate(cards, "card_refresh.png", 272 + 535 * slot, 644);
        }
        Complete(1);
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

        var steps = new List<byte[]> { list, detail, empty, detail, list, detail };
        if (made)
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
        var service = new CharacterRequestService(auto, log, ocr)
        {
            RestockFromList = (_, target, _, _) =>
            {
                calls++;
                Assert.Equal(("1080001600", "810", 2, 2000), (target.UnitId, target.Route.SeriesId, target.Route.Tab, target.Route.Capital));
                return Task.FromResult(made);
            }
        };
        var result = await service.RunFromListAsync("test", default);
        Assert.Equal(1, calls);
        Assert.Equal(eligible ? 1 : 0, result.Reported);
        Assert.Equal(eligible ? 0 : 1, result.FailedRoles.Count);
        Assert.Equal(steps.Count, adb.Taps.Count);
    }

    [Fact]
    public void Delivery_ValidatesCostLevelAndDuplicateProduction()
    {
        var v = new DeliveryOffer("1175000100", "V高达", 1) { RoleId = "1175000100", Number = 5 };
        var route = Assert.IsType<DeliveryTarget>(DevelopmentRequestIndex.ForDelivery(v, new())).Route;
        Assert.Equal(("1750", 55, 27000), (route.SeriesId, route.Level, route.Capital));
        Assert.Null(DevelopmentRequestIndex.ForDelivery(v with { MinimumLevel = 56 }, new()));
        Assert.Null(DevelopmentRequestIndex.ForDelivery(v with { UnitId = "0" }, new()));
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var period = DeliveryProductionAttempt.RefreshPeriod;
            Assert.True(new DeliveryProductionAttempt("device", 1, v, period, dir.FullName).Reserve());
            Assert.False(new DeliveryProductionAttempt("device", 1, v, period, dir.FullName).Reserve());
            Assert.False(new DeliveryProductionAttempt("device", 0, v, period - 1, dir.FullName).Reserve());
        }
        finally { dir.Delete(true); }
    }
}
