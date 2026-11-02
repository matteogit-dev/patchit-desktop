namespace PatchIt.Client.Models;

public sealed class Game
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public string Version { get; set; } = "";
    public string UpdatedDate { get; set; } = "";
    public double? Progress { get; set; }
    public string DownloadUrl { get; set; } = "";
    public string DownloadType { get; set; } = "none";
    public string CoverImage { get; set; } = "";
    public string SteamUrl { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Instructions { get; set; } = [];
    public List<ChangelogEntry> Changelog { get; set; } = [];
    public string Sha256 { get; set; } = "";
}

public sealed class ChangelogEntry
{
    public string Version { get; set; } = "";
    public string Date { get; set; } = "";
    public string Note { get; set; } = "";
}
