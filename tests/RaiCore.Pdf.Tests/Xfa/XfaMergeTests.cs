using RaiCore.Pdf.Xfa;
using static RaiCore.Pdf.Tests.Xfa.Xdp;

namespace RaiCore.Pdf.Tests.Xfa;

/// <summary>Merging data into the template: bindings, repeated subforms, exclusion groups, defaults and what is not shown.</summary>
public class XfaMergeTests
{
    private static string Page(string xdp, int page = 0) => Pages(Convert(xdp))[page];

    [Fact]
    public void Fields_bind_to_data_values_of_their_name_once_each()
    {
        var xdp = Form(Field("Name") + Field("Name") + Field("City"), "<form1><Name>First</Name><Name>Second</Name><City>Oslo</City></form1>");

        var text = Page(xdp);
        Assert.True(text.IndexOf("First", StringComparison.Ordinal) < text.IndexOf("Second", StringComparison.Ordinal));
        Assert.Contains("Oslo", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Subforms_bind_to_data_groups_and_unnamed_ones_pass_the_data_on()
    {
        var xdp = Form(
            $"""<subform name="Person" layout="tb">{Field("Name")}</subform><subform layout="tb">{Field("Name")}</subform>""",
            "<form1><Person><Name>Inner</Name></Person><Name>Outer</Name></form1>");

        var text = Page(xdp);
        Assert.Contains("Inner", text, StringComparison.Ordinal);
        Assert.Contains("Outer", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_subform_without_its_data_group_matches_its_content_in_the_current_one()
    {
        var xdp = Form($"""<subform name="Missing" layout="tb">{Field("Name")}</subform>""", "<form1><Name>Flat</Name></form1>");

        Assert.Contains("Flat", Page(xdp), StringComparison.Ordinal);
    }

    [Fact]
    public void Repeated_subforms_get_an_instance_per_data_group_within_their_maximum()
    {
        var rows = string.Concat(Enumerable.Range(1, 5).Select(i => $"<Row><V>Value{i}</V></Row>"));
        var xdp = Form($"""<subform name="Row" layout="tb"><occur min="1" max="3"/>{Field("V")}</subform>""", $"<form1>{rows}</form1>");

        var text = Page(xdp);
        Assert.Contains("Value3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Value4", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repeated_subform_has_at_least_its_minimum_instances()
    {
        var xdp = Form($"""<subform name="Row" layout="tb"><occur min="3" max="-1"/>{Draw("Line")}</subform>""", "<form1><Row/></form1>");

        Assert.Equal(3, Page(xdp).Split("Line").Length - 1);
    }

    [Fact]
    public void Without_data_a_repeated_subform_has_its_initial_instances()
    {
        var xdp = Form($"""<subform name="Row" layout="tb"><occur min="0" max="-1" initial="2"/>{Draw("Line")}</subform>""");

        Assert.Equal(2, Page(xdp).Split("Line").Length - 1);
    }

    [Fact]
    public void Without_data_a_repeated_subform_has_one_instance_unless_it_says_otherwise()
    {
        var xdp = Form($"""<subform name="Row" layout="tb"><occur min="0" max="-1"/>{Draw("Line")}</subform>""");

        Assert.Equal(1, Page(xdp).Split("Line").Length - 1);
    }

    [Fact]
    public void Global_fields_share_the_first_value_of_their_name()
    {
        var bind = "<bind match=\"global\"/>";
        var xdp = Form(
            $"""<subform name="A" layout="tb">{Field("Company", content: bind)}</subform><subform name="B" layout="tb">{Field("Company", content: bind)}</subform>""",
            "<form1><A/><B/><Info><Company>Acme</Company></Info></form1>");

        Assert.Equal(2, Page(xdp).Split("Acme").Length - 1);
    }

    [Fact]
    public void Data_references_follow_their_expression()
    {
        var items = "<Items><Item><Name>One</Name></Item><Item><Name>Two</Name></Item></Items>";
        var xdp = Form(
            $"""
            {Field("Ref", content: "<bind match=\"dataRef\" ref=\"$record.Meta.Code\"/>")}
            <subform name="Line" layout="tb"><occur min="0" max="-1"/><bind match="dataRef" ref="$.Items.Item[*]"/>{Field("Name")}</subform>
            """,
            $"<form1><Meta><Code>X-1</Code></Meta>{items}</form1>");

        var text = Page(xdp);
        Assert.Contains("X-1", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("One", StringComparison.Ordinal) < text.IndexOf("Two", StringComparison.Ordinal));
    }

    [Fact]
    public void Fields_bound_to_nothing_keep_their_default()
    {
        var xdp = Form(Field("Name", content: "<bind match=\"none\"/><value><text>Default</text></value>"), "<form1><Name>Data</Name></form1>");

        var text = Page(xdp);
        Assert.Contains("Default", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Data", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Data_no_field_takes_is_reported()
    {
        var (_, diagnostics) = ConvertWithDiagnostics(Form(Field("Name"), "<form1><Name>A</Name><Extra>B</Extra></form1>"));

        Assert.Contains(diagnostics, d => d.Kind == XfaDiagnosticKind.Data && d.Message.Contains("Extra", StringComparison.Ordinal));
    }

    [Fact]
    public void Hidden_and_unprinted_objects_are_left_out_and_invisible_ones_keep_their_space()
    {
        var xdp = Form(
            Draw("Hidden", attributes: "presence=\"hidden\"") + Draw("ScreenOnly", attributes: "relevant=\"-print\"")
            + Draw("Invisible", attributes: "presence=\"invisible\"") + Draw("Shown"));

        var pdf = Convert(xdp);
        var text = Pages(pdf)[0];
        Assert.DoesNotContain("Hidden", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ScreenOnly", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Invisible", text, StringComparison.Ordinal);

        // The invisible draw keeps its 0.3 inch in the flow: "Shown" starts 0.3 inch below the content area's top.
        Assert.InRange(792 - 36 - Find(pdf, 0, "Shown").Top, 21.6, 23);
    }

    [Fact]
    public void Exclusion_groups_turn_on_the_button_whose_value_the_data_has()
    {
        var button = "<field name=\"{0}\" w=\"1in\" h=\"0.3in\"><ui><checkButton shape=\"round\"/></ui><items><text>{0}</text></items></field>";
        var xdp = Form(
            $"""<exclGroup name="Pick" layout="tb">{string.Format(System.Globalization.CultureInfo.InvariantCulture, button, "A")}{string.Format(System.Globalization.CultureInfo.InvariantCulture, button, "B")}</exclGroup>""",
            "<form1><Pick>B</Pick></form1>");

        // A round button's mark is a filled circle: drawn once, for B (the circles of both buttons are stroked).
        var content = Slides.PptxToPdfTests.Content(Convert(xdp));
        Assert.Equal(1, content.Split("\nf\n").Length - 1);
    }

    [Fact]
    public void Exclusion_groups_give_each_button_its_on_or_off_value()
    {
        var xdp = Form(
            """<exclGroup name="Pick" layout="tb"><field name="A"><ui><checkButton/></ui><items><text>a</text><text>none</text></items></field><field name="B"><ui><checkButton/></ui><items><text>b</text><text>none</text></items></field></exclGroup>""",
            "<form1><Pick>b</Pick></form1>");
        var xml = new RaiCore.Pdf.Xfa.Packets.XfaXml(ResourceLimits.Default);
        var packets = RaiCore.Pdf.Xfa.Packets.XfaPackets.FromXdp(xml.Load(xdp), null, []);

        var form = RaiCore.Pdf.Xfa.XfaConverter.Merge(packets, new XfaConverterOptions(), xml, null);
        var group = Assert.Single(form.Root.Children);
        Assert.Equal(["none", "b"], group.Children.Select(f => f.Value));
    }

    [Fact]
    public void Check_boxes_are_checked_when_the_data_has_their_on_value()
    {
        string Box(string value) => Form("""<field name="Agree" w="1in" h="0.3in"><ui><checkButton/></ui><items><integer>1</integer><integer>0</integer></items></field>""", $"<form1><Agree>{value}</Agree></form1>");

        var on = Slides.PptxToPdfTests.Content(Convert(Box("1")));
        var off = Slides.PptxToPdfTests.Content(Convert(Box("0")));
        Assert.True(on.Split(" l\n").Length > off.Split(" l\n").Length, "The check mark adds lines.");
    }

    [Fact]
    public void Choice_lists_show_the_text_of_the_saved_value()
    {
        var xdp = Form(
            """<field name="Color" w="2in" h="0.3in"><ui><choiceList/></ui><items><text>Red</text><text>Green</text></items><items save="1"><text>r</text><text>g</text></items></field>""",
            "<form1><Color>g</Color></form1>");

        Assert.Contains("Green", Page(xdp), StringComparison.Ordinal);
    }

    [Fact]
    public void Rich_text_data_is_shown_with_its_formatting()
    {
        var xdp = Form(
            """<field name="Notes" w="4in" h="1in"><ui><textEdit allowRichText="1" multiLine="1"/></ui><font typeface="Arial"/></field>""",
            $"""<form1><Notes xfa:contentType="text/html" xmlns:xfa="{DataNamespace}"><body xmlns="http://www.w3.org/1999/xhtml"><p>Plain <b>bold</b></p><p>Second</p></body></Notes></form1>""");

        var pdf = Convert(xdp);
        var text = Pages(pdf)[0];
        Assert.Contains("Plain bold", text, StringComparison.Ordinal);
        Assert.Contains("LiberationSans-Bold", System.Text.Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
        Assert.Contains("Second", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Prototypes_give_their_properties_to_the_objects_that_use_them()
    {
        var xdp = Form($"""
            <proto><draw id="big" w="3in" h="1in"><font typeface="Arial" size="20pt"/><para hAlign="right"/><value><text>From prototype</text></value></draw></proto>
            <draw use="#big" h="2in"/>{Draw("Below")}
            """);

        // The font, the width (the text is right-aligned in 3 inches) and the value come from the prototype; the
        // height the draw sets itself wins.
        var pdf = Convert(xdp);
        var text = Find(pdf, 0, "From prototype");
        Assert.InRange(text.Height, 15, 25);
        Assert.InRange(text.Right, 36 + 216 - 3, 36 + 216 + 1);
        Assert.InRange(756 - Find(pdf, 0, "Below").Top, 144 - 1, 144 + 4);
    }
}
