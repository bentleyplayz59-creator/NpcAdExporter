using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using xTile;
using xTile.Layers;
using xTile.Tiles;

namespace NpcAdExporter;

/// <summary>On save load, exports everything needed to build NPC shop ads (no other mods required).</summary>
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

    /// <summary>Pixels of a tilesheet, cached while rendering maps.</summary>
    private class SheetData
    {
        public Color[] Pixels;
        public int Width;
        public int Height;
    }

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
            $"Files are in: {Path.Combine(this.Helper.DirectoryPath, "export")}",
            LogLevel.Alert
        );
    }

    private bool ExportMap(string assetName, HashSet<string> images)
    {
        Map map;
        try
        {
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

        // layout JSON (backup, lets us rebuild the map from data)
        this.DumpMapLayout(assetName, map);

        // full rendered picture of the map
        return this.RenderMap(assetName, map);
    }

    private bool ExportImage(string assetName)
    {
        try
        {
            Texture2D texture = this.Helper.GameContent.Load<Texture2D>(assetName);
            string path = this.GetOutputPath(assetName, ".png");
            using (FileStream stream = File.Create(path))
                texture.SaveAsPng(stream, texture.Width, texture.Height);
            return true;
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Couldn't export image '{assetName}': {ex.Message}", LogLevel.Warn);
            return false;
        }
    }

    /// <summary>Draw every visible layer of the map into one PNG.</summary>
    private bool RenderMap(string assetName, Map map)
    {
        try
        {
            const int TileSize = 16;

            int widthTiles = 0, heightTiles = 0;
            foreach (Layer layer in map.Layers)
            {
                widthTiles = Math.Max(widthTiles, layer.LayerWidth);
                heightTiles = Math.Max(heightTiles, layer.LayerHeight);
            }

            int width = widthTiles * TileSize;
            int height = heightTiles * TileSize;
            if (width <= 0 || height <= 0)
                return false;

            var canvas = new Color[width * height];
            var sheets = new Dictionary<string, SheetData>();

            foreach (Layer layer in map.Layers)
            {
                // the Paths layer is a marker layer, not drawn in-game
                if (string.Equals(layer.Id, "Paths", StringComparison.OrdinalIgnoreCase))
                    continue;

                for (int y = 0; y < layer.LayerHeight; y++)
                {
                    for (int x = 0; x < layer.LayerWidth; x++)
                    {
                        Tile tile = layer.Tiles[x, y];
                        if (tile == null || tile.TileSheet == null)
                            continue;

                        SheetData sheet = this.GetSheetData(tile.TileSheet, sheets);
                        if (sheet == null)
                            continue;

                        int columns = sheet.Width / TileSize;
                        if (columns <= 0)
                            continue;

                        int srcX0 = (tile.TileIndex % columns) * TileSize;
                        int srcY0 = (tile.TileIndex / columns) * TileSize;

                        for (int py = 0; py < TileSize; py++)
                        {
                            int srcY = srcY0 + py;
                            if (srcY >= sheet.Height)
                                break;

                            for (int px = 0; px < TileSize; px++)
                            {
                                int srcX = srcX0 + px;
                                if (srcX >= sheet.Width)
                                    break;

                                Color src = sheet.Pixels[srcY * sheet.Width + srcX];
                                if (src.A == 0)
                                    continue;

                                int dstIndex = (y * TileSize + py) * width + (x * TileSize + px);
                                if (src.A == 255)
                                {
                                    canvas[dstIndex] = src;
                                    continue;
                                }

                                // pixels from the game are premultiplied, so blend "over"
                                Color dst = canvas[dstIndex];
                                int inv = 255 - src.A;
                                canvas[dstIndex] = new Color(
                                    Math.Min(255, src.R + dst.R * inv / 255),
                                    Math.Min(255, src.G + dst.G * inv / 255),
                                    Math.Min(255, src.B + dst.B * inv / 255),
                                    Math.Min(255, src.A + dst.A * inv / 255)
                                );
                            }
                        }
                    }
                }
            }

            using (var texture = new Texture2D(StardewValley.Game1.graphics.GraphicsDevice, width, height))
            {
                texture.SetData(canvas);
                string path = this.GetOutputPath(assetName, ".rendered.png");
                using (FileStream stream = File.Create(path))
                    texture.SaveAsPng(stream, width, height);
            }

            return true;
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Couldn't render map '{assetName}': {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    private SheetData GetSheetData(TileSheet sheet, Dictionary<string, SheetData> cache)
    {
        if (cache.TryGetValue(sheet.Id, out SheetData cached))
            return cached;

        SheetData data = null;
        try
        {
            Texture2D texture = this.Helper.GameContent.Load<Texture2D>(this.NormalizeSheetPath(sheet.ImageSource));
            var pixels = new Color[texture.Width * texture.Height];
            texture.GetData(pixels);
            data = new SheetData { Pixels = pixels, Width = texture.Width, Height = texture.Height };
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Couldn't read tilesheet '{sheet.ImageSource}': {ex.Message}", LogLevel.Warn);
        }

        cache[sheet.Id] = data;
        return data;
    }

    /// <summary>Get the output file path for an asset (creating its folder).</summary>
    private string GetOutputPath(string assetName, string extension)
    {
        string relative = assetName.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar);
        string fullPath = Path.Combine(this.Helper.DirectoryPath, "export", relative + extension);
        string folder = Path.GetDirectoryName(fullPath);
        if (folder != null)
            Directory.CreateDirectory(folder);
        return fullPath;
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
