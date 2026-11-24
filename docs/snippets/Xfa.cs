#pragma warning disable CA1849, CA2007
using RaiCore.Pdf;
using RaiCore.Pdf.Xfa;

namespace Snippets;

internal static class Xfa
{
    public static async Task RunAsync()
    {
        // A small XFA form to convert: an XDP file with a template (a repeated row) and its data. A PDF file with an XFA
        // form converts the same way.
        await File.WriteAllTextAsync("form.xdp", """
            <xdp:xdp xmlns:xdp="http://ns.adobe.com/xdp/">
            <template xmlns="http://www.xfa.org/schema/xfa-template/3.3/">
              <subform name="form1" layout="tb" locale="en_US">
                <pageSet><pageArea name="Page1"><contentArea x="0.5in" y="0.5in" w="7.5in" h="10in"/><medium stock="letter" short="8.5in" long="11in"/></pageArea></pageSet>
                <field name="Name" w="4in" h="0.4in"><ui><textEdit/></ui><font typeface="Arial"/>
                  <caption reserve="1in"><value><text>Name</text></value></caption></field>
                <subform name="Item" layout="lr-tb" w="7.5in"><occur min="0" max="-1"/>
                  <field name="Text" w="5in" h="0.3in"><ui><textEdit/></ui><font typeface="Arial"/></field>
                  <field name="Price" w="2in" h="0.3in"><ui><numericEdit/></ui><font typeface="Arial"/><para hAlign="right"/>
                    <format><picture>num{$z,zz9.99}</picture></format></field>
                </subform>
              </subform>
            </template>
            <xfa:datasets xmlns:xfa="http://www.xfa.org/schema/xfa-data/1.0/"><xfa:data>
              <form1><Name>Ada Lovelace</Name><Item><Text>Analytical engine</Text><Price>1834</Price></Item><Item><Text>Punched cards</Text><Price>12.5</Price></Item></form1>
            </xfa:data></xfa:datasets>
            </xdp:xdp>
            """);

        #region convert
        var pdf = XfaToPdf.Convert("form.xdp");
        pdf.Save("form-flattened.pdf");
        #endregion

        #region options
        var options = new XfaConverterOptions
        {
            Data = "<form1><Name>Grace Hopper</Name></form1>",   // Data merged instead of the form's own.
            Title = "Application",                               // The PDF file's own title by default.
            Limits = ResourceLimits.Default with { MaxPages = 50, Timeout = TimeSpan.FromSeconds(20) },
        };
        await using (var input = File.OpenRead("form.xdp"))
        {
            var document = await XfaToPdf.ConvertAsync(input, options);
            document.Save("form-other-data.pdf");
        }
        #endregion

        #region diagnostics
        var report = XfaToPdf.Convert("form.xdp", new XfaConverterOptions
        {
            Diagnostics = d => Console.WriteLine($"{d.Kind}: {d.Message}"),
        });
        report.Save("form-report.pdf");
        #endregion
    }
}
