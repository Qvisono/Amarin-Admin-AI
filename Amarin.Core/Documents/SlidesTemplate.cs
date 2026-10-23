using System.Text;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;

namespace Amarin.Core;

/// <summary>
/// Каркас новой презентации 16:9: тема, мастер слайдов, две раскладки («Титульный» и «Заголовок и
/// объект») и мастер заметок.
/// </summary>
/// <remarks>
/// <para>
/// Каркас написан разметкой, а не деревом классов OpenXml: так он читается как файл PowerPoint и
/// в пять раз короче. Слайды же строятся деревом (<see cref="SlideBuilder"/>) — в них текст
/// модели, и экранирование берёт на себя библиотека.
/// </para>
/// <para>
/// Мастер заметок нужен всегда, когда у слайда есть заметки: без него PowerPoint предлагает
/// «восстановить» файл. Поэтому его умеет доставлять и редактор (<see cref="EnsureNotesMaster"/>) —
/// у чужой презентации его может не быть.
/// </para>
/// </remarks>
internal static class SlidesTemplate
{
    /// <summary>16:9, как у новой презентации PowerPoint.</summary>
    public const long Width = 12_192_000;

    public const long Height = 6_858_000;

    private const string Namespaces =
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";

    private const string GroupHeader =
        "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
        "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

    private const string ColorMap =
        "bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" " +
        "accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"";

    private const string Theme = $"""
        <a:theme {Namespaces} name="Amarin">
          <a:themeElements>
            <a:clrScheme name="Amarin">
              <a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1>
              <a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1>
              <a:dk2><a:srgbClr val="1F2937"/></a:dk2>
              <a:lt2><a:srgbClr val="F3F4F6"/></a:lt2>
              <a:accent1><a:srgbClr val="2563EB"/></a:accent1>
              <a:accent2><a:srgbClr val="F59E0B"/></a:accent2>
              <a:accent3><a:srgbClr val="10B981"/></a:accent3>
              <a:accent4><a:srgbClr val="EF4444"/></a:accent4>
              <a:accent5><a:srgbClr val="8B5CF6"/></a:accent5>
              <a:accent6><a:srgbClr val="0EA5E9"/></a:accent6>
              <a:hlink><a:srgbClr val="2563EB"/></a:hlink>
              <a:folHlink><a:srgbClr val="7C3AED"/></a:folHlink>
            </a:clrScheme>
            <a:fontScheme name="Amarin">
              <a:majorFont><a:latin typeface="Segoe UI Semibold"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont>
              <a:minorFont><a:latin typeface="Segoe UI"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont>
            </a:fontScheme>
            <a:fmtScheme name="Amarin">
              <a:fillStyleLst>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
              </a:fillStyleLst>
              <a:lnStyleLst>
                <a:ln w="9525"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln>
                <a:ln w="12700"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln>
                <a:ln w="19050"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln>
              </a:lnStyleLst>
              <a:effectStyleLst>
                <a:effectStyle><a:effectLst/></a:effectStyle>
                <a:effectStyle><a:effectLst/></a:effectStyle>
                <a:effectStyle><a:effectLst/></a:effectStyle>
              </a:effectStyleLst>
              <a:bgFillStyleLst>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
              </a:bgFillStyleLst>
            </a:fmtScheme>
          </a:themeElements>
          <a:objectDefaults/>
          <a:extraClrSchemeLst/>
        </a:theme>
        """;

    private static string Level(int level, int indent, int size) =>
        $"<a:lvl{level}pPr marL=\"{indent * level}\" indent=\"-{indent / 2}\"><a:spcBef><a:spcPts val=\"600\"/></a:spcBef>" +
        "<a:buFont typeface=\"Arial\"/><a:buChar char=\"&#8226;\"/>" +
        $"<a:defRPr sz=\"{size}\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl{level}pPr>";

    private static readonly string Master = $"""
        <p:sldMaster {Namespaces}>
          <p:cSld>
            <p:bg><p:bgRef idx="1001"><a:schemeClr val="bg1"/></p:bgRef></p:bg>
            <p:spTree>
              {GroupHeader}
              <p:sp>
                <p:nvSpPr><p:cNvPr id="2" name="Title Placeholder 1"/><p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
                <p:spPr><a:xfrm><a:off x="609600" y="365125"/><a:ext cx="10972800" cy="1325563"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr>
                <p:txBody><a:bodyPr vert="horz" anchor="ctr"><a:normAutofit/></a:bodyPr><a:lstStyle/><a:p><a:endParaRPr lang="en-US"/></a:p></p:txBody>
              </p:sp>
              <p:sp>
                <p:nvSpPr><p:cNvPr id="3" name="Text Placeholder 2"/><p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr><p:nvPr><p:ph type="body" idx="1"/></p:nvPr></p:nvSpPr>
                <p:spPr><a:xfrm><a:off x="609600" y="1825625"/><a:ext cx="10972800" cy="4351338"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr>
                <p:txBody><a:bodyPr vert="horz"><a:normAutofit/></a:bodyPr><a:lstStyle/><a:p><a:endParaRPr lang="en-US"/></a:p></p:txBody>
              </p:sp>
            </p:spTree>
          </p:cSld>
          <p:clrMap {ColorMap}/>
          <p:sldLayoutIdLst>
            <p:sldLayoutId id="2147483649" r:id="{TitleLayoutId}"/>
            <p:sldLayoutId id="2147483650" r:id="{ContentLayoutId}"/>
          </p:sldLayoutIdLst>
          <p:txStyles>
            <p:titleStyle>
              <a:lvl1pPr algn="l"><a:defRPr sz="3600"><a:solidFill><a:schemeClr val="tx1"/></a:solidFill><a:latin typeface="+mj-lt"/><a:ea typeface="+mj-ea"/><a:cs typeface="+mj-cs"/></a:defRPr></a:lvl1pPr>
            </p:titleStyle>
            <p:bodyStyle>
              {Level(1, 342900, 2400)}{Level(2, 342900, 2000)}{Level(3, 342900, 1800)}{Level(4, 342900, 1600)}{Level(5, 342900, 1600)}
            </p:bodyStyle>
            <p:otherStyle>
              <a:lvl1pPr><a:defRPr sz="1800"><a:solidFill><a:schemeClr val="tx1"/></a:solidFill><a:latin typeface="+mn-lt"/></a:defRPr></a:lvl1pPr>
            </p:otherStyle>
          </p:txStyles>
        </p:sldMaster>
        """;

    private const string TitleLayout = $"""
        <p:sldLayout {Namespaces} type="title" preserve="1">
          <p:cSld name="Title Slide">
            <p:spTree>
              {GroupHeader}
              <p:sp>
                <p:nvSpPr><p:cNvPr id="2" name="Title 1"/><p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr><p:nvPr><p:ph type="ctrTitle"/></p:nvPr></p:nvSpPr>
                <p:spPr><a:xfrm><a:off x="1524000" y="1122363"/><a:ext cx="9144000" cy="2387600"/></a:xfrm></p:spPr>
                <p:txBody><a:bodyPr anchor="b"/><a:lstStyle><a:lvl1pPr algn="ctr"><a:defRPr sz="4400"/></a:lvl1pPr></a:lstStyle><a:p><a:endParaRPr lang="en-US"/></a:p></p:txBody>
              </p:sp>
              <p:sp>
                <p:nvSpPr><p:cNvPr id="3" name="Subtitle 2"/><p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr><p:nvPr><p:ph type="subTitle" idx="1"/></p:nvPr></p:nvSpPr>
                <p:spPr><a:xfrm><a:off x="1524000" y="3602038"/><a:ext cx="9144000" cy="1655762"/></a:xfrm></p:spPr>
                <p:txBody><a:bodyPr/><a:lstStyle><a:lvl1pPr marL="0" indent="0" algn="ctr"><a:buNone/><a:defRPr sz="2000"/></a:lvl1pPr></a:lstStyle><a:p><a:endParaRPr lang="en-US"/></a:p></p:txBody>
              </p:sp>
            </p:spTree>
          </p:cSld>
          <p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>
        </p:sldLayout>
        """;

    private const string ContentLayout = $"""
        <p:sldLayout {Namespaces} type="obj" preserve="1">
          <p:cSld name="Title and Content">
            <p:spTree>
              {GroupHeader}
              <p:sp>
                <p:nvSpPr><p:cNvPr id="2" name="Title 1"/><p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
                <p:spPr/>
                <p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:endParaRPr lang="en-US"/></a:p></p:txBody>
              </p:sp>
              <p:sp>
                <p:nvSpPr><p:cNvPr id="3" name="Content Placeholder 2"/><p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr><p:nvPr><p:ph idx="1"/></p:nvPr></p:nvSpPr>
                <p:spPr/>
                <p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:endParaRPr lang="en-US"/></a:p></p:txBody>
              </p:sp>
            </p:spTree>
          </p:cSld>
          <p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>
        </p:sldLayout>
        """;

    private const string NotesMaster = $"""
        <p:notesMaster {Namespaces}>
          <p:cSld>
            <p:spTree>
              {GroupHeader}
              <p:sp>
                <p:nvSpPr><p:cNvPr id="2" name="Slide Image Placeholder 1"/><p:cNvSpPr><a:spLocks noGrp="1" noRot="1" noChangeAspect="1"/></p:cNvSpPr><p:nvPr><p:ph type="sldImg" idx="2"/></p:nvPr></p:nvSpPr>
                <p:spPr><a:xfrm><a:off x="685800" y="1143000"/><a:ext cx="5486400" cy="3086100"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr>
              </p:sp>
              <p:sp>
                <p:nvSpPr><p:cNvPr id="3" name="Notes Placeholder 2"/><p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr><p:nvPr><p:ph type="body" sz="quarter" idx="3"/></p:nvPr></p:nvSpPr>
                <p:spPr><a:xfrm><a:off x="685800" y="4400550"/><a:ext cx="5486400" cy="3600450"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr>
                <p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:endParaRPr lang="en-US"/></a:p></p:txBody>
              </p:sp>
            </p:spTree>
          </p:cSld>
          <p:clrMap {ColorMap}/>
        </p:notesMaster>
        """;

    private const string TitleLayoutId = "rIdLayoutTitle";

    private const string ContentLayoutId = "rIdLayoutContent";

    /// <summary>Каркас в пустом пакете; слайды добавляет <see cref="SlideBuilder"/>.</summary>
    public static PresentationPart Create(PresentationDocument document)
    {
        var presentation = document.AddPresentationPart();
        var master = presentation.AddNewPart<SlideMasterPart>("rIdMaster");
        var theme = master.AddNewPart<ThemePart>("rIdTheme");
        Feed(theme, Theme);
        presentation.AddPart(theme, "rIdTheme");

        var title = master.AddNewPart<SlideLayoutPart>(TitleLayoutId);
        Feed(title, TitleLayout);
        title.AddPart(master);
        var content = master.AddNewPart<SlideLayoutPart>(ContentLayoutId);
        Feed(content, ContentLayout);
        content.AddPart(master);
        Feed(master, Master);

        presentation.Presentation = new P.Presentation(
            new P.SlideMasterIdList(new P.SlideMasterId { Id = 2147483648U, RelationshipId = "rIdMaster" }),
            new P.SlideIdList(),
            new P.SlideSize { Cx = (int)Width, Cy = (int)Height },
            new P.NotesSize { Cx = 6_858_000, Cy = 9_144_000 },
            new P.DefaultTextStyle())
        { SaveSubsetFonts = true };
        return presentation;
    }

    /// <summary>Мастер заметок презентации; нет его — создаётся по образцу каркаса.</summary>
    public static NotesMasterPart EnsureNotesMaster(PresentationPart presentation)
    {
        if (presentation.NotesMasterPart is { } existing)
        {
            return existing;
        }

        var notes = presentation.AddNewPart<NotesMasterPart>();
        Feed(notes, NotesMaster);
        var theme = presentation.ThemePart ?? presentation.SlideMasterParts.FirstOrDefault()?.ThemePart;
        if (theme is not null)
        {
            notes.AddPart(theme);
        }
        else
        {
            Feed(notes.AddNewPart<ThemePart>(), Theme);
        }

        var root = presentation.Presentation ?? throw new DocumentException("The PowerPoint file has no presentation part.");
        var list = new P.NotesMasterIdList(new P.NotesMasterId { Id = presentation.GetIdOfPart(notes) });

        // Порядок детей презентации — по схеме: список мастеров заметок сразу за мастерами слайдов.
        if (root.SlideMasterIdList is { } masters)
        {
            masters.InsertAfterSelf(list);
        }
        else
        {
            root.PrependChild(list);
        }

        return notes;
    }

    private static void Feed(OpenXmlPart part, string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        part.FeedData(stream);
    }
}
