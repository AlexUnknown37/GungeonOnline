using UnityEngine;

namespace GungeonOnline
{
    // The other players down the right side of the screen: their character, name, health (hearts and armor) and
    // the blanks they carry.
    // One heart container per heart of max health, wrapping onto more rows, so a player who finds more hearts gets
    // a longer bar. Everything is drawn in pixel steps that grow with the window, like the game's own HUD.
    public static class PartyList
    {
        private const int HeartsPerRow = 8;

        private static Texture2D heart, shield, blank, white;

        private static readonly string[] HeartShape =
        {
            " XX XX ",
            "XXXXXXX",
            "XXXXXXX",
            " XXXXX ",
            "  XXX  ",
            "   X   ",
        };
        private static readonly string[] ShieldShape =
        {
            "XXXXXXX",
            "XXXXXXX",
            "XXXXXXX",
            "XXXXXXX",
            " XXXXX ",
            "  XXX  ",
            "   X   ",
        };

        private static readonly string[] BlankShape =
        {
            " XXX ",
            "XXXXX",
            "XXXXX",
            "XXXXX",
            " XXX ",
        };

        private static readonly Color Full = new Color(0.92f, 0.13f, 0.15f);
        private static readonly Color BlankColor = new Color(0.55f, 0.85f, 1f);
        private static readonly Color Empty = new Color(0.22f, 0.22f, 0.25f);
        private static readonly Color ArmorColor = new Color(0.35f, 0.65f, 1f);

        private static GUIStyle nameStyle;

        public static void Draw(NetSession session)
        {
            if (session == null || session.Status != NetSession.State.Connected || !GameManager.HasInstance
                || GameManager.Instance.IsLoadingLevel || GameManager.Instance.IsPaused || Event.current.type != EventType.Repaint
                || LevelSync.BookOpen())
                return;
            EnsureTextures();
            Collect(session);
            if (entries.Count == 0)
                return;

            // One "game pixel" on screen. Lots of hearts make the list taller: shrink it to fit below its top.
            float px = Mathf.Max(1f, Screen.height / 450f);
            float top = Screen.height * 0.30f;
            float room = Screen.height * 0.97f - top;
            float needed = 0f;
            foreach (Entry e in entries)
                needed += EntryHeight(e, 1f) + 3f;
            px = Mathf.Min(px, room / needed);

            float icon = 22f * px;
            float heartStep = 9f * px;
            float rightEdge = Screen.width - 8f * px;
            float width = icon + 4f * px + HeartsPerRow * heartStep;
            float x = rightEdge - width;
            float y = top;

            if (nameStyle == null)
            {
                nameStyle = new GUIStyle(GUI.skin.label);
                nameStyle.fontStyle = FontStyle.Bold;
                nameStyle.clipping = TextClipping.Clip;
            }
            nameStyle.fontSize = Mathf.Max(6, Mathf.RoundToInt(7f * px));

            foreach (Entry e in entries)
            {
                string name = e.Name;
                float health = e.Health, max = e.Max, armor = e.Armor;
                bool ghost = e.Ghost, bot = e.Bot;
                tk2dSpriteDefinition look = e.Look;
                int containers = Containers(e), shields = Shields(e);
                float nameH = 9f * px;
                float rowH = EntryHeight(e, px);

                // Backing panel.
                GUI.color = new Color(0f, 0f, 0f, 0.45f);
                GUI.DrawTexture(new Rect(x - 3f * px, y - 2f * px, width + 6f * px, rowH), white);

                // Character.
                GUI.color = ghost ? new Color(0.55f, 0.65f, 1f, 0.6f) : Color.white;
                DrawSprite(look, new Rect(x, y, icon, icon));

                // Name.
                float tx = x + icon + 4f * px;
                GUI.color = Color.black;
                GUI.Label(new Rect(tx + px, y - 2f * px + px, width - icon, nameH + 4f * px), Label(name, ghost, bot), nameStyle);
                GUI.color = ghost ? new Color(0.7f, 0.7f, 0.75f) : Color.white;
                GUI.Label(new Rect(tx, y - 2f * px, width - icon, nameH + 4f * px), Label(name, ghost, bot), nameStyle);

                // Hearts, then armor, wrapping every HeartsPerRow.
                int i = 0;
                if (FindRealSprites())
                {
                    GUI.color = ghost ? new Color(1f, 1f, 1f, 0.6f) : Color.white;
                    for (int h = 0; h < containers; h++, i++)
                    {
                        float fill = Mathf.Clamp01(health - h);
                        DrawReal(fill >= 0.99f ? realFull : fill > 0.01f ? realHalf : realEmpty, Cell(tx, y + nameH, i, heartStep, px));
                    }
                    for (int a = 0; a < shields; a++, i++)
                        DrawReal(realArmor, Cell(tx, y + nameH, i, heartStep, px));
                    int realBlankRow = i == 0 ? HeartsPerRow : (i + HeartsPerRow - 1) / HeartsPerRow * HeartsPerRow;
                    for (int b = 0; b < Blanks(e); b++)
                        DrawReal(realBlank, Cell(tx, y + nameH, realBlankRow + b, heartStep, px));
                    y += rowH + 3f * px;
                    continue;
                }
                for (int h = 0; h < containers; h++, i++)
                {
                    Rect cell = Cell(tx, y + nameH, i, heartStep, px);
                    float fill = Mathf.Clamp01(health - h);
                    GUI.color = Empty;
                    GUI.DrawTexture(cell, heart);
                    if (fill >= 0.99f)
                    {
                        GUI.color = Full;
                        GUI.DrawTexture(cell, heart);
                    }
                    else if (fill > 0.01f)
                    {
                        GUI.color = Full;
                        GUI.DrawTextureWithTexCoords(new Rect(cell.x, cell.y, cell.width * 0.5f, cell.height), heart, new Rect(0f, 0f, 0.5f, 1f));
                    }
                }
                for (int a = 0; a < shields; a++, i++)
                {
                    GUI.color = ArmorColor;
                    GUI.DrawTexture(Cell(tx, y + nameH, i, heartStep, px), shield);
                }
                // Blanks on a row of their own below.
                int blankRow = (i + HeartsPerRow - 1) / HeartsPerRow * HeartsPerRow;
                if (i == 0) blankRow = HeartsPerRow;
                for (int b = 0; b < Blanks(e); b++)
                {
                    GUI.color = BlankColor;
                    Rect c = Cell(tx, y + nameH, blankRow + b, heartStep, px);
                    GUI.DrawTexture(new Rect(c.x + c.width * 0.15f, c.y + c.height * 0.15f, c.width * 0.7f, c.height * 0.7f), blank);
                }
                y += rowH + 3f * px;
            }
            GUI.color = Color.white;
        }

        private class Entry
        {
            public string Name;
            public float Health, Max, Armor;
            public int Blanks;
            public bool Ghost, Bot;
            public tk2dSpriteDefinition Look;
        }
        private static readonly System.Collections.Generic.List<Entry> entries = new System.Collections.Generic.List<Entry>();
        private static readonly Entry[] pool = { new Entry(), new Entry(), new Entry(), new Entry() };

        // Everyone to list (not us): connected players, and on the host a stand-in bot.
        private static void Collect(NetSession session)
        {
            entries.Clear();
            for (int slot = 0; slot < Protocol.MaxPlayers; slot++)
            {
                if (slot == session.LocalSlot)
                    continue;
                Entry e = pool[slot];
                RemotePlayerState r = session.Players[slot];
                if (r.Connected && r.HasPlayer)
                {
                    e.Name = r.Name;
                    e.Health = r.Health;
                    e.Max = r.MaxHealth;
                    e.Armor = r.Armor;
                    e.Blanks = r.Blanks;
                    e.Ghost = r.IsDead;
                    e.Bot = r.OnBreak;
                    e.Look = Plugin.Puppets[slot] != null ? Plugin.Puppets[slot].Icon() : null;
                }
                else if (StandIn.Active && StandIn.Slot == slot && StandIn.Player != null)
                {
                    PlayerController p = StandIn.Player;
                    e.Name = StandIn.Name;
                    e.Health = p.healthHaver.GetCurrentHealth();
                    e.Max = p.healthHaver.GetMaxHealth();
                    e.Armor = p.healthHaver.Armor;
                    e.Blanks = p.Blanks;
                    e.Ghost = p.IsGhost || p.healthHaver.IsDead;
                    e.Bot = true;
                    e.Look = IdleOf(p);
                }
                else
                    continue;
                entries.Add(e);
            }
        }

        private static int Containers(Entry e)
        {
            return Mathf.Clamp(Mathf.CeilToInt(e.Max - 0.01f), 0, 200);
        }

        private static int Shields(Entry e)
        {
            return Mathf.Clamp(Mathf.CeilToInt(e.Armor - 0.01f), 0, 200);
        }

        private static int Blanks(Entry e)
        {
            return Mathf.Clamp(e.Blanks, 0, HeartsPerRow);
        }

        // How tall one player's block is at a given pixel size (picture, or name plus rows of hearts).
        private static float EntryHeight(Entry e, float px)
        {
            int rows = Mathf.Max(1, Mathf.CeilToInt((Containers(e) + Shields(e)) / (float)HeartsPerRow)) + (Blanks(e) > 0 ? 1 : 0);
            return Mathf.Max(22f * px, 9f * px + rows * 9f * px) + 4f * px;
        }

        private static string Label(string name, bool ghost, bool bot)
        {
            string n = string.IsNullOrEmpty(name) ? "Player" : name;
            return ghost ? n + " (ghost)" : bot ? n + " (bot)" : n;
        }

        private static Rect Cell(float x, float y, int index, float step, float px)
        {
            int row = index / HeartsPerRow, col = index % HeartsPerRow;
            return new Rect(x + col * step, y + row * step, step - px, step - px);
        }

        private static tk2dSpriteDefinition IdleOf(PlayerController p)
        {
            tk2dSpriteAnimator anim = p.spriteAnimator;
            if (anim != null && anim.Library != null)
            {
                foreach (string clipName in new string[] { "idle_front", "idle", "idle_forward" })
                {
                    tk2dSpriteAnimationClip clip = anim.Library.GetClipByName(clipName);
                    if (clip != null && clip.frames != null && clip.frames.Length > 0 && clip.frames[0].spriteCollection != null)
                        return clip.frames[0].spriteCollection.spriteDefinitions[clip.frames[0].spriteId];
                }
            }
            return p.sprite != null ? p.sprite.GetCurrentSpriteDef() : null;
        }

        // Draw one sprite from its sheet, centered in the box and kept pixel-sharp.
        private static void DrawSprite(tk2dSpriteDefinition def, Rect box)
        {
            if (def == null || def.uvs == null || def.uvs.Length < 4)
                return;
            Material m = def.materialInst != null ? def.materialInst : def.material;
            Texture tex = m != null ? m.mainTexture : null;
            if (tex == null)
                return;
            Vector2 min = def.uvs[0], max = def.uvs[0];
            foreach (Vector2 uv in def.uvs)
            {
                min = Vector2.Min(min, uv);
                max = Vector2.Max(max, uv);
            }
            bool rotated = def.flipped != tk2dSpriteDefinition.FlipMode.None;
            float w = (max.x - min.x) * tex.width, h = (max.y - min.y) * tex.height;
            if (rotated)
            {
                float t = w; w = h; h = t;
            }
            if (w <= 0f || h <= 0f)
                return;
            float scale = Mathf.Min(box.width / w, box.height / h);
            if (scale >= 1f)
                scale = Mathf.Floor(scale);
            float dw = w * scale, dh = h * scale;
            Rect r = new Rect(box.x + (box.width - dw) * 0.5f, box.y + (box.height - dh), dw, dh);
            Rect uvRect = new Rect(min.x, min.y, max.x - min.x, max.y - min.y);
            if (!rotated)
            {
                GUI.DrawTextureWithTexCoords(r, tex, uvRect);
                return;
            }
            // Packed sideways on the sheet: draw it turned back upright.
            Matrix4x4 saved = GUI.matrix;
            Vector2 c = r.center;
            GUIUtility.RotateAroundPivot(-90f, c);
            GUI.DrawTextureWithTexCoords(new Rect(c.x - dh * 0.5f, c.y - dw * 0.5f, dh, dw), tex, uvRect);
            GUI.matrix = saved;
        }

        // The game's own HUD pictures (full/half/empty heart, armor, blank), taken from its UI atlas.
        private struct RealSprite
        {
            public Texture2D Texture;
            public Rect Uv;
            public Vector2 Size;
        }
        private static RealSprite realFull, realHalf, realEmpty, realArmor, realBlank;
        private static bool haveReal;
        private static float nextRealLookAt;

        private static bool FindRealSprites()
        {
            if (haveReal)
                return realFull.Texture != null;
            if (Time.realtimeSinceStartup < nextRealLookAt)
                return false;
            nextRealLookAt = Time.realtimeSinceStartup + 2f;
            try
            {
                GameUIRoot root = GameUIRoot.Instance;
                if (root == null || root.heartControllers == null || root.heartControllers.Count == 0
                    || root.blankControllers == null || root.blankControllers.Count == 0)
                    return false;
                GameUIHeartController hearts = root.heartControllers[0];
                dfSprite blankPrefab = root.blankControllers[0].blankSpritePrefab;
                if (hearts.heartSpritePrefab == null || hearts.armorSpritePrefab == null || blankPrefab == null)
                    return false;
                dfAtlas atlas = hearts.heartSpritePrefab.Atlas;
                bool ok = Real(atlas, hearts.fullHeartSpriteName, out realFull)
                    & Real(atlas, hearts.halfHeartSpriteName, out realHalf)
                    & Real(atlas, hearts.emptyHeartSpriteName, out realEmpty)
                    & Real(hearts.armorSpritePrefab.Atlas, hearts.armorSpritePrefab.SpriteName, out realArmor)
                    & Real(blankPrefab.Atlas, blankPrefab.SpriteName, out realBlank);
                Plugin.Log.LogInfo("Team HUD: the game's heart/armor/blank pictures " + (ok ? "found" : "not found; using drawn ones")
                    + " (" + hearts.fullHeartSpriteName + ", " + hearts.armorSpritePrefab.SpriteName + ", " + blankPrefab.SpriteName + ").");
                if (!ok)
                    realFull.Texture = null;
                haveReal = true;
                return ok;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning("Team HUD: couldn't read the game's HUD pictures: " + ex.Message);
                haveReal = true;
                realFull.Texture = null;
                return false;
            }
        }

        private static bool Real(dfAtlas atlas, string name, out RealSprite sprite)
        {
            sprite = new RealSprite();
            if (atlas == null || string.IsNullOrEmpty(name) || atlas.Texture == null)
                return false;
            dfAtlas.ItemInfo item = atlas[name];
            if (item == null || item.rotated)
                return false;
            sprite.Texture = atlas.Texture;
            sprite.Uv = item.region;
            sprite.Size = item.sizeInPixels;
            return true;
        }

        // Fit the picture into the cell, bottom-centered (filling it: whole-number steps left them tiny).
        private static void DrawReal(RealSprite s, Rect cell)
        {
            if (s.Texture == null || s.Size.x <= 0f || s.Size.y <= 0f)
                return;
            float scale = Mathf.Min(cell.width / s.Size.x, cell.height / s.Size.y);
            float w = s.Size.x * scale, h = s.Size.y * scale;
            GUI.DrawTextureWithTexCoords(new Rect(cell.x + (cell.width - w) * 0.5f, cell.y + (cell.height - h), w, h), s.Texture, s.Uv);
        }

        private static void EnsureTextures()
        {
            if (heart == null)
                heart = Shape(HeartShape);
            if (shield == null)
                shield = Shape(ShieldShape);
            if (blank == null)
                blank = Shape(BlankShape);
            if (white == null)
            {
                white = new Texture2D(1, 1);
                white.SetPixel(0, 0, Color.white);
                white.Apply();
            }
        }

        // A small pixel-art shape: white inside (tinted when drawn) with a black outline around it.
        private static Texture2D Shape(string[] rows)
        {
            int h = rows.Length + 2, w = rows[0].Length + 2;
            bool[,] fill = new bool[w, h];
            for (int y = 0; y < rows.Length; y++)
                for (int x = 0; x < rows[y].Length; x++)
                    fill[x + 1, h - 2 - y] = rows[y][x] == 'X';
            Texture2D t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            t.filterMode = FilterMode.Point;
            t.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    Color c = new Color(0f, 0f, 0f, 0f);
                    if (fill[x, y])
                        c = Color.white;
                    else
                    {
                        for (int dy = -1; dy <= 1 && c.a == 0f; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, ny = y + dy;
                                if (nx >= 0 && ny >= 0 && nx < w && ny < h && fill[nx, ny])
                                {
                                    c = Color.black;
                                    break;
                                }
                            }
                    }
                    t.SetPixel(x, y, c);
                }
            t.Apply();
            return t;
        }
    }
}
