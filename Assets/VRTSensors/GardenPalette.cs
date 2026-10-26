using UnityEngine;

/// <summary>
/// The colours everything in the garden agrees on. One colour per signal, used wherever
/// that signal is spoken of - the board, the signpost beside the giant, the effect itself -
/// so a child can follow "the green one" from the sign to the leaves without reading.
/// The board's own surfaces are bark and cream, so it belongs to the garden rather than
/// to a control room.
/// </summary>
public static class GardenPalette
{
    // One per signal.
    public static readonly Color Knock = new Color(1f, 0.93f, 0.70f);     // pale gold: the ring in the grass
    public static readonly Color Wind = new Color(0.66f, 0.90f, 0.45f);    // leaf green: the leaves that fly
    public static readonly Color Warmth = new Color(1f, 0.55f, 0.18f);     // amber: the cracks when warm
    public static readonly Color Cool = new Color(0.35f, 0.62f, 0.98f);    // the cracks when calm
    public static readonly Color Heart = new Color(1f, 0.50f, 0.58f);      // petal rose: the breathing
    public static readonly Color Pulse = new Color(0.85f, 0.12f, 0.22f);   // crimson: the heart-stone in the chest
    public static readonly Color Snow = new Color(0.86f, 0.94f, 1f);       // ice: the snow
    public static readonly Color Night = new Color(0.50f, 0.42f, 0.90f);   // indigo: the night sky
    public static readonly Color Muscle = new Color(0.35f, 0.82f, 0.80f);  // turquoise: the fists
    public static readonly Color Dream = new Color(0.96f, 0.72f, 0.95f);   // lilac: the dream

    // The board and the signs.
    public static readonly Color Panel = new Color(0.16f, 0.12f, 0.08f, 0.94f);   // bark
    public static readonly Color Surface = new Color(0.24f, 0.19f, 0.13f);        // lighter bark
    public static readonly Color Ink = new Color(0.98f, 0.95f, 0.86f);            // cream
    public static readonly Color Dim = new Color(0.76f, 0.69f, 0.56f);
    public static readonly Color Live = new Color(0.66f, 0.90f, 0.45f);           // leaf, same as Wind
    public static readonly Color Warn = new Color(0.98f, 0.80f, 0.36f);           // pollen
    public static readonly Color Bad = new Color(0.96f, 0.50f, 0.42f);
    public static readonly Color Bark = new Color(0.45f, 0.33f, 0.23f);
    public static readonly Color Stone = new Color(0.52f, 0.54f, 0.58f);
    public static readonly Color NightSky = new Color(0.07f, 0.08f, 0.22f);       // the sky once night has fallen
    public static readonly Color Frost = new Color(0.78f, 0.88f, 0.90f);          // the grass under snow
}
