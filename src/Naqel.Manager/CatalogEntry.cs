namespace Naqel.Manager;

/// <summary>
/// A plugin a catalog source offers.
/// </summary>
/// <param name="Id">Its package id, which is also the plugin's id</param>
/// <param name="Version">Its newest version</param>
/// <param name="Title">Its name, to show</param>
/// <param name="Description">What it does, to show</param>
/// <param name="Authors">Who made it</param>
/// <param name="Source">The source it's from</param>
/// <param name="Compatible">Whether it's built for the plugin contracts this Naqel has, as its package says</param>
public sealed record CatalogEntry(string Id, string Version, string Title, string? Description, string? Authors, string Source, bool Compatible);
