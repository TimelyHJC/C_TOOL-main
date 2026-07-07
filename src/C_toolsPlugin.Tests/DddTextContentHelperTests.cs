using Autodesk.AutoCAD.DatabaseServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C_toolsDddPlugin;

namespace C_toolsPlugin.Tests;

[TestClass]
public class DddTextContentHelperTests
{
    [TestMethod]
    public void NormalizeLineEndings_ReturnsOriginalTextWhenNoCarriageReturnExists()
    {
        const string text = "第一行\n第二行";

        var normalized = DddTextContentHelper.NormalizeLineEndings(text);

        Assert.AreSame(text, normalized);
    }

    [TestMethod]
    public void NormalizeLineEndings_ConvertsMixedCarriageReturnsToLineFeeds()
    {
        const string text = "第一行\r\n第二行\r第三行";

        var normalized = DddTextContentHelper.NormalizeLineEndings(text);

        Assert.AreEqual("第一行\n第二行\n第三行", normalized);
    }

    [TestMethod]
    public void ToEditableText_ForMText_ReplacesParagraphMarkersAfterNormalizingLineEndings()
    {
        const string rawText = "甲\\P乙\r\n丙";

        var editable = DddTextContentHelper.ToEditableText(rawText, isMText: true);

        Assert.AreEqual("甲\n乙\n丙", editable);
    }

    [TestMethod]
    public void ToMTextContents_EscapesSpecialCharactersAndUsesParagraphMarkers()
    {
        const string editableText = "A\\B{C}\r\nD";

        var contents = DddTextContentHelper.ToMTextContents(editableText);

        Assert.AreEqual(@"A\\B\{C\}\PD", contents);
    }
}
