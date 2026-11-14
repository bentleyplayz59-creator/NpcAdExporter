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
using Game1 = StardewValley.Game1;

namespace NpcAdExporter;

/// <summary>Exports everything needed to build NPC shop ads. Runs once when a world loads, and any time you tap the on-screen button.</summary>
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
    private bool AutoRan;
    private bool Running;
    private string Status = "Export Ad Assets";

    private Queue<string> MapQueue;
    private Queue<string> ImageQueue;
    private HashSet<string> Images;
    private int Ok, Failed, MapsTotal, MapsDone, ImagesTotal, ImagesDone, LastReported;

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
        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.Display.RenderedHud += this.OnRenderedHud;
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;

        helper.ConsoleCommands.Add("exportads", "Runs the NPC ad asset export.", (name, args) =>
            this.StartExport());

        this.Monitor.Log(
            $"Ready. Nothing runs until you load a world. Output folder: {this.GetExportFolder()}",
            LogLevel.Alert
        );
    }

    /*********
    ** Event handlers
    *********/
    /// <summary>Runs the export once automatically, the first time a world starts.</summary>
    private void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        if (this.AutoRan)
            return;
        this.AutoRan = true;

        this.StartExport();
    }

    /// <summary>Does a little of the export work each tick, so progress shows up on screen while it runs.</summary>
    private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        if (!this.Running)
            return;

        if (!Context.IsWorldReady)
        {
            this.Running = false;
            this.Status = "Export stopped (left world)";
            return;
        }

        // stage 1: maps (one per tick)
        if (this.MapQueue.Count > 0)
        {
            string name = this.MapQueue.Dequeue();
            bool good = this.ExportMap(name, this.Images);
            this.MapsDone++;
            if (good)
                this.Ok++;
            else
                this.Failed++;

            this.Status = $"Exporting maps {this.MapsDone}/{this.MapsTotal}";
            this.Say($"{(good ? "OK" : "FAILED")}: {name}", !good);
            return;
        }

        // stage 2: images (a few per tick)
        if (this.ImageQueue == null)
        {
            this.ImageQueue = new Queue<string>(this.Images.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            this.ImagesTotal = this.ImageQueue.Count;
            this.Say($"Maps done. Exporting {this.ImagesTotal} images (portraits, sprites, tilesheets, item sheets)...");
            return;
        }

        if (this.ImageQueue.Count > 0)
        {
            for (int i = 0; i < 3 && this.ImageQueue.Count > 0; i++)
            {
                string name = this.ImageQueue.Dequeue();
                bool good = this.ExportImage(name);
                this.ImagesDone++;
                if (good)
                    this.Ok++;
                else
                {
                    this.Failed++;
                    this.Say($"FAILED: {name}", true);
                }
            }

            this.Status = $"Exporting images {this.ImagesDone}/{this.ImagesTotal}";
            if (this.ImagesDone - this.LastReported >= 15)
            {
                this.LastReported = this.ImagesDone;
                this.Say($"Images: {this.ImagesDone}/{this.ImagesTotal}");
            }
            return;
        }

        // finished
        this.Running = false;
        this.Status = $"Done: {this.Ok} ok, {this.Failed} failed. Tap to rerun";
        this.Say($"DONE. {this.Ok} exported, {this.Failed} failed. Files are in: {this.GetExportFolder()}");
    }

    /// <summary>Draws the on-screen button.</summary>
    private void OnRenderedHud(object sender, RenderedHudEventArgs e)
    {
        if (!this.IsButtonVisible())
            return;

        Rectangle rect = this.GetButtonRect();
        SpriteBatch batch = e.SpriteBatch;
        Color fill = this.Running ? new Color(130, 85, 0) : new Color(20, 95, 45);

        batch.Draw(Game1.staminaRect, new Rectangle(rect.X - 4, rect.Y - 4, rect.Width + 8, rect.Height + 8), Color.White);
        batch.Draw(Game1.staminaRect, rect, fill * 0.92f);

        Vector2 size = Game1.smallFont.MeasureString(this.Status);
        batch.DrawString(
            Game1.smallFont,
            this.Status,
            new Vector2(rect.X + (rect.Width - size.X) / 2f, rect.Y + (rect.Height - size.Y) / 2f),
            Color.White
        );
    }

    /// <summary>Starts the export when the on-screen button is tapped.</summary>
    private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
    {
        if (e.Button != SButton.MouseLeft || !this.IsButtonVisible())
            return;

        int mouseX = Game1.getMouseX(true);
        int mouseY = Game1.getMouseY(true);
        if (this.GetButtonRect().Contains(mouseX, mouseY))
        {
            this.Helper.Input.Suppress(e.Button);
            this.StartExport();
        }
    }

    /*********
    ** Private methods
    *********/
    private bool IsButtonVisible()
    {
        return Context.IsWorldReady && Game1.activeClickableMenu == null && !Game1.eventUp;
    }

    private Rectangle GetButtonRect()
    {
        Vector2 size = Game1.smallFont.MeasureString(this.Status);
        int width = Math.Max(260, (int)size.X + 40);
        int height = 72;
        return new Rectangle(16, Game1.uiViewport.Height / 3, width, height);
    }

    private string GetExportFolder()
    {
        return Path.Combine(this.Helper.DirectoryPath, "export");
    }

    /// <summary>Show a message in the in-game chat (and the SMAPI log).</summary>
    private void Say(string text, bool isError = false)
    {
        this.Monitor.Log(text, isError ? LogLevel.Warn : LogLevel.Info);

        try
        {
            if (Game1.chatBox != null)
                Game1.chatBox.addInfoMessage("[Ad Exporter] " + text);
        }
        catch (Exception)
        {
            // chat isn't critical
        }
    }

    private void StartExport()
    {
        if (!Context.IsWorldReady)
        {
            this.Monitor.Log("Load a world first, then try again.", LogLevel.Warn);
            return;
        }

        if (this.Running)
        {
            this.Say("Export is already running...");
            return;
        }

        this.Images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string npc in Npcs)
        {
            this.Images.Add($"Portraits/{npc}");
            this.Images.Add($"Characters/{npc}");
        }
        foreach (string name in SharedImages)
            this.Images.Add(name);

        IEnumerable<string> maps = IncludeExteriors ? InteriorMaps.Concat(ExteriorMaps) : InteriorMaps;
        this.MapQueue = new Queue<string>(maps);
        this.ImageQueue = null;
        this.MapsTotal = this.MapQueue.Count;
        this.MapsDone = 0;
        this.ImagesTotal = 0;
        this.ImagesDone = 0;
        this.LastReported = 0;
        this.Ok = 0;
        this.Failed = 0;

        Directory.CreateDirectory(this.GetExportFolder());

        this.Running = true;
        this.Status = "Exporting...";
        this.Say($"Export started: {this.MapsTotal} maps, then images. Folder: {this.GetExportFolder()}");
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
