using O2C.Core.Exclusions;

namespace O2C.Core.Tests;

public class ExclusionTextEditorTests
{
    [Fact]
    public void Adds_single_entry_normalized()
    {
        var r = ExclusionTextEditor.Add("", "  Example.COM ");
        Assert.Equal("example.com", r.Text);
        Assert.Equal(["example.com"], r.Added);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void Paste_list_with_mixed_separators_and_comments()
    {
        var r = ExclusionTextEditor.Add("# офис\n10.0.0.1", "2ip.ru, *.2ip.io; 188.40.167.5/24\n10.0.0.1 # уже есть\nbad_host!");
        Assert.Equal(["2ip.ru", "*.2ip.io", "188.40.167.0/24"], r.Added);
        Assert.Equal(1, r.Duplicates);
        Assert.Single(r.Errors);
        Assert.Equal("# офис\n10.0.0.1\n2ip.ru\n*.2ip.io\n188.40.167.0/24", r.Text);
    }

    [Fact]
    public void Nothing_added_keeps_text()
    {
        var r = ExclusionTextEditor.Add("a.example.com\n", "A.example.com");
        Assert.Empty(r.Added);
        Assert.Equal(1, r.Duplicates);
        Assert.Equal("a.example.com", r.Text);
    }

    [Fact]
    public void Removes_entry_keeping_comments_and_other_lines()
    {
        const string text = "# офис\n10.0.0.1\n2ip.ru # проверка IP\n*.2ip.io";
        Assert.Equal("# офис\n10.0.0.1\n*.2ip.io", ExclusionTextEditor.Remove(text, "2ip.ru"));
    }

    [Fact]
    public void Removes_by_normalized_form()
    {
        Assert.Equal("a.example.com", ExclusionTextEditor.Remove("a.example.com\n10.1.2.3/24", "10.1.2.0/24"));
        Assert.Equal("", ExclusionTextEditor.Remove("EXAMPLE.com.", "example.com"));
    }

    [Fact]
    public void Removes_invalid_line_by_raw_text()
    {
        Assert.Equal("ok.example.com", ExclusionTextEditor.Remove("ok.example.com\nbad_host!", "bad_host!"));
    }
}
