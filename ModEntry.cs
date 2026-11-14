using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using xTile;
using xTile.Layers;
using xTile.Tiles;

namespace NpcAdExporter;

/// <summary>On save load, runs Content Patcher's "patch export" for everything needed to build NPC shop ads.</summary>
public class ModEntry : Mod
{
    /*********
    ** Settings (edit these, then rebuild)
    *********/
    /// <summary>Also export the exterior maps (Town, Forest, Beach, Mountain, Desert).</summary>
    private static readonly bool IncludeExteriors = true;

    /// <summary>NPCs to export a portrait + sprite sheet for.</summary>
    private static readonly string[] Npcs = { "Robin", "Marnie", "Willy", "Clint", "Gus", "Harvey", "Sandy" };

    /// <summary>Shop interiors: Robin, Marnie, Willy, Clint, Gus, Harvey's clinic, Sandy.</summary>
    private static readonly string[] InteriorMaps =
    {
        "Maps/ScienceHouse", "Maps/AnimalShop", "Maps/FishShop", "Maps/Blacksmith",
        "Maps/Saloon", "Maps/Hospital", "Maps/SandyHouse"
    };

    private static readonly string[] ExteriorMaps =
    {
        "Maps/Mountain", "Maps/Forest", "Maps/Beach", "Maps/Town", "Maps/Desert"
    };

    /// <summary>Shared sheets for item pop-ups.</summary>
    private static readonly string[] SharedImages =
    {
        "Maps/springobjects", "TileSheets/Craftables", "TileSheets/tools"
    };

    /*********
    ** Fields
    *********/
    private bool Done;

    /*********
    ** Public methods
    *********/
    public override void Entry(IModHelper helper)
    {
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
    }

    /*********
    ** Private methods
    *********/
    private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
    {
        if (this.Done)
            return;
        this.Done = true;

        this.ExportAll();
    }

    private void ExportAll()
    {
        // image assets to export (tilesheets get added as maps are processed)
        var images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string npc in Npcs)
        {
            images.Add($"Portraits/{npc}");
            images.Add($"Characters/{npc}");
        }
        foreach (string name in SharedImages)
            images.Add(name);

        // maps
        IEnumerable<string> maps = IncludeExteriors ? InteriorMaps.Concat(ExteriorMaps) : InteriorMaps;
        int mapsOk = 0, mapsFailed = 0;
        foreach (string mapName in maps)
        {
            if (this.ExportMap(mapName, images))
                mapsOk++;
            else
                mapsFailed++;
        }

        // images (portraits, sprites, shared sheets, and every tilesheet the maps use)
        int imagesOk = 0, imagesFailed = 0;
        foreach (string imageName in images.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (this.ExportImage(imageName))
                imagesOk++;
            else
                imagesFailed++;
        }

        this.Monitor.Log(
            $"Done. Maps: {mapsOk} ok, {mapsFailed} failed. Images: {imagesOk} ok, {imagesFailed} failed. " +
            "Content Patcher files are in the 'patch export' folder in your game folder; " +
            $"map layout JSON is in '{this.Helper.DirectoryPath}/export'.",
            LogLevel.Alert
        );
    }

    private bool ExportMap(string assetName, HashSet<string> images)
    {
        Map map;
        try
        {
            // loading it first means Content Patcher can detect the asset type
            map = this.Helper.GameContent.Load<Map>(assetName);
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Couldn't load map '{assetName}': {ex.Message}", LogLevel.Error);
            return false;
        }

        // queue every tilesheet the map uses
        foreach (TileSheet sheet in map.TileSheets)
        {
            if (!string.IsNullOrWhiteSpace(sheet.ImageSource))
                images.Add(this.NormalizeSheetPath(sheet.ImageSource));
        }

        // Content Patcher export
        bool exported = this.RunPatchExport(assetName, null);

        // fallback: our own dump of the map layout, in case the map doesn't export as a usable file
        this.DumpMapLayout(assetName, map);

        return exported;
    }

    private bool ExportImage(string assetName)
    {
        try
        {
            this.Helper.GameContent.Load<Texture2D>(assetName);
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Couldn't load image '{assetName}': {ex.Message}", LogLevel.Warn);
            return false;
        }

        return this.RunPatchExport(assetName, "image");
    }

    private bool RunPatchExport(string assetName, string type)
    {
        string[] args = type == null
            ? new[] { "export", assetName }
            : new[] { "export", assetName, type };

        try
        {
            bool found = this.Helper.ConsoleCommands.Trigger("patch", args);
            if (!found)
                this.Monitor.Log($"'patch' command not found (is Content Patcher installed?) for '{assetName}'.", LogLevel.Error);
            return found;
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"patch export failed for '{assetName}': {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    /// <summary>Turn a tilesheet's image source into an asset name like 'Maps/townInterior'.</summary>
    private string NormalizeSheetPath(string source)
    {
        string path = source.Trim().Replace('\\', '/');
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            path = path.Substring(0, path.Length - 4);
        if (!path.Contains('/'))
            path = "Maps/" + path;
        return path;
    }

    /// <summary>Write the map's size, tilesheets, properties, tile indexes and tile properties to JSON.</summary>
    private void DumpMapLayout(string assetName, Map map)
    {
        try
        {
            var sheets = new List<string>();
            foreach (TileSheet sheet in map.TileSheets)
                sheets.Add($"{sheet.Id} = {sheet.ImageSource}");

            var mapProps = new List<string>();
            foreach (var prop in map.Properties)
                mapProps.Add($"{prop.Key} = {prop.Value}");

            var layers = new List<object>();
            var tileProps = new List<string>();
            foreach (Layer layer in map.Layers)
            {
                var rows = new List<string>();
                for (int y = 0; y < layer.LayerHeight; y++)
                {
                    var cells = new List<string>();
                    for (int x = 0; x < layer.LayerWidth; x++)
                    {
                        Tile tile = layer.Tiles[x, y];
                        if (tile == null)
                        {
                            cells.Add("");
                            continue;
                        }

                        cells.Add($"{tile.TileSheet.Id}:{tile.TileIndex}");

                        foreach (var prop in tile.Properties)
                            tileProps.Add($"{layer.Id} {x},{y} {prop.Key} = {prop.Value}");
                    }
                    rows.Add(string.Join(",", cells));
                }

                layers.Add(new { Id = layer.Id, Width = layer.LayerWidth, Height = layer.LayerHeight, Rows = rows });
            }

            var data = new
            {
                Asset = assetName,
                TileSheets = sheets,
                MapProperties = mapProps,
                TileProperties = tileProps,
                Layers = layers
            };

            string fileName = assetName.Replace('/', '_').Replace('\\', '_');
            this.Helper.Data.WriteJsonFile($"export/{fileName}.layout.json", data);
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Couldn't dump layout for '{assetName}': {ex.Message}", LogLevel.Warn);
        }
    }
}
