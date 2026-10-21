using System.Net;
using O2C.Core.Exclusions;

namespace O2C.Core.Tests;

public class AppExclusionTests
{
    private static ExclusionEntry Entry(string text)
    {
        var result = ExclusionParser.Parse(text);
        Assert.Empty(result.Errors);
        return Assert.Single(result.Entries);
    }

    [Fact]
    public void Exe_path_is_application_file()
    {
        var e = Entry(@"C:\Games\Foo\foo.exe");
        Assert.Equal(ExclusionKind.Application, e.Kind);
        Assert.False(e.IsFolder);
        Assert.Equal(@"C:\Games\Foo\foo.exe", e.AppPath);
    }

    [Theory]
    [InlineData(@"C:\Games\Foo", @"C:\Games\Foo\")]
    [InlineData(@"C:\Games\Foo\", @"C:\Games\Foo\")]
    [InlineData(@"C:/Games/Foo/", @"C:\Games\Foo\")]
    [InlineData(@"""C:\Program Files\Foo Bar\""", @"C:\Program Files\Foo Bar\")]
    [InlineData(@"C:\Games\Foo\..\Bar", @"C:\Games\Bar\")]
    public void Folder_is_normalized_with_trailing_backslash(string input, string expected)
    {
        var e = Entry(input);
        Assert.True(e.IsFolder);
        Assert.Equal(expected, e.AppPath);
        Assert.Equal(expected, e.Text);
    }

    [Fact]
    public void Hash_inside_path_is_not_a_comment()
    {
        var e = Entry(@"C:\Games\C#\game.exe   # любимая игра");
        Assert.Equal(@"C:\Games\C#\game.exe", e.AppPath);
    }

    [Theory]
    [InlineData(@"C:\", "диск")]
    [InlineData("game.exe", "полный путь")]
    [InlineData(@"C:\Games\*.exe", "Некорректный путь")]
    public void Bad_application_entries(string input, string messagePart)
    {
        var error = Assert.Single(ExclusionParser.Parse(input).Errors);
        Assert.Contains(messagePart, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\Games\Foo\foo.exe", @"C:\Games\Foo\foo.exe", true)]
    [InlineData(@"C:\Games\Foo\foo.exe", @"c:\games\foo\FOO.EXE", true)]
    [InlineData(@"C:\Games\Foo\foo.exe", @"C:\Games\Foo\bar.exe", false)]
    [InlineData(@"C:\Games\Foo\", @"C:\Games\Foo\bin\game.exe", true)]
    [InlineData(@"C:\Games\Foo\", @"C:\Games\FooBar\game.exe", false)]
    [InlineData(@"C:\Games\Foo\", @"D:\Games\Foo\game.exe", false)]
    public void Application_matching(string entry, string appPath, bool expected) =>
        Assert.Equal(expected, DnsNames.MatchesApp(Entry(entry), appPath));

    [Fact]
    public void Paste_keeps_paths_with_spaces_whole()
    {
        var r = ExclusionTextEditor.Add("", "C:\\Program Files\\Foo Bar\\foo.exe\nexample.com, 10.0.0.1\n\"C:\\Games\\Baz\"");
        Assert.Equal([@"C:\Program Files\Foo Bar\foo.exe", "example.com", "10.0.0.1", @"C:\Games\Baz\"], r.Added);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void Remove_application_entry()
    {
        const string text = "example.com\nC:\\Games\\Foo\\   # папка игры\n10.0.0.1";
        Assert.Equal("example.com\n10.0.0.1", ExclusionTextEditor.Remove(text, @"C:\Games\Foo\"));
    }
}

public class AppAddressBookTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Game = @"C:\Games\Foo\foo.exe";

    [Fact]
    public void Learn_reports_only_new_addresses()
    {
        var book = new AppAddressBook();
        Assert.True(book.Learn(Game, IPAddress.Parse("203.0.113.1"), T0));
        Assert.False(book.Learn(Game, IPAddress.Parse("203.0.113.1"), T0.AddMinutes(1)));
        Assert.True(book.Learn(Game, IPAddress.Parse("203.0.113.2"), T0));
        Assert.Equal(2, book.Get(Game).Count);
    }

    [Fact]
    public void Addresses_expire_after_seven_days_unless_touched()
    {
        var book = new AppAddressBook();
        book.Learn(Game, IPAddress.Parse("203.0.113.1"), T0);
        book.Learn("other", IPAddress.Parse("203.0.113.9"), T0);
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Game, "other" };

        book.Touch(Game, T0.AddDays(6));   // программа была запущена на 6-й день
        book.Prune(T0.AddDays(8), entries);
        Assert.Single(book.Get(Game));       // продлено касанием
        Assert.Empty(book.Get("other"));     // не трогали 8 дней — забыто

        book.Prune(T0.AddDays(14), entries);
        Assert.Empty(book.Get(Game));
    }

    [Fact]
    public void Removed_entries_are_forgotten()
    {
        var book = new AppAddressBook();
        book.Learn(Game, IPAddress.Parse("203.0.113.1"), T0);
        book.Prune(T0, new HashSet<string>());
        Assert.Empty(book.Get(Game));
    }

    [Fact]
    public void Cap_drops_oldest()
    {
        var book = new AppAddressBook();
        for (int i = 0; i < AppAddressBook.MaxPerEntry + 5; i++)
            book.Learn(Game, new IPAddress([10, 1, (byte)(i / 256), (byte)(i % 256)]), T0.AddSeconds(i));
        var addresses = book.Get(Game);
        Assert.Equal(AppAddressBook.MaxPerEntry, addresses.Count);
        Assert.DoesNotContain(IPAddress.Parse("10.1.0.0"), addresses);
        Assert.Contains(IPAddress.Parse("10.1.4.4"), addresses);
    }

    [Fact]
    public void Survives_serialization()
    {
        var book = new AppAddressBook();
        book.Learn(Game, IPAddress.Parse("203.0.113.1"), T0);
        book.Learn(Game, IPAddress.Parse("2001:db8::1"), T0);
        Assert.True(book.Dirty);
        var json = book.Serialize();
        Assert.False(book.Dirty);

        var restored = AppAddressBook.Deserialize(json);
        Assert.Equal(2, restored.Get(Game).Count);
        restored.Prune(T0.AddDays(8), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Game });
        Assert.Empty(restored.Get(Game));   // дата сохранилась — срок считается верно
    }
}
