using System.Windows.Documents;

namespace Clawbrower.Models;

/// <summary>
/// Parsed markdown block — paragraph, header, list, table, or divider.
/// </summary>
public abstract class MdBlock { }

/// <summary>Regular paragraph or formatted text line</summary>
public class MdParagraph : MdBlock
{
    public List<Inline> Inlines { get; set; } = new();
}

/// <summary>Header line (## text)</summary>
public class MdHeader : MdBlock
{
    public string Text { get; set; } = "";
    public int Level { get; set; } = 1; // 1-6 → # to ######
}

/// <summary>Unordered or ordered list</summary>
public class MdList : MdBlock
{
    public List<string> Items { get; set; } = new();
    public bool Ordered { get; set; }
    public int IndentLevel { get; set; }
}

/// <summary>Blockquote</summary>
public class MdQuote : MdBlock
{
    public string Text { get; set; } = "";
}

/// <summary>Table with header + rows</summary>
public class MdTable : MdBlock
{
    /// <summary>Header row cell texts</summary>
    public List<string> Headers { get; set; } = new();
    /// <summary>Data rows, each row is a list of cell texts</summary>
    public List<List<string>> Rows { get; set; } = new();
    /// <summary>Column alignments: null = default, "left"/"center"/"right"</summary>
    public List<string?> Alignments { get; set; } = new();
}

/// <summary>Horizontal divider</summary>
public class MdDivider : MdBlock { }
