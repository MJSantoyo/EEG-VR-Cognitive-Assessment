using System;
using UnityEngine;

namespace IkeaEeg.Interaction
{
    /// <summary>Colour dimension of a chair. Also drives the material used at build time.</summary>
    public enum ChairColor
    {
        Red,
        Blue,
        Green,
        Yellow,
        White,
        Black,
    }

    /// <summary>Size dimension of a chair. Drives a uniform scale factor.</summary>
    public enum ChairSize
    {
        Small,
        Medium,
        Large,
    }

    /// <summary>
    /// Backrest-structure dimension of a chair. Drives the placeholder geometry variant.
    ///
    /// WHY THESE LABELS: the previous values were Modern / Classic / Rounded. "Modern" and
    /// "Classic" are aesthetic judgements — they require cultural or design knowledge, two
    /// people can reasonably disagree about which chair is which, and a participant cannot
    /// derive them from the object in front of them. That makes them unusable as an
    /// experimental attribute: an error would not distinguish "failed to find the chair" from
    /// "interpreted the word differently".
    ///
    /// The three values below name the STRUCTURE OF THE BACKREST, which is the most salient
    /// part of each body and is directly visible from the participant's standing position:
    ///
    ///   SOLID   — the backrest is ONE continuous flat panel, with no gaps and no curvature.
    ///   SLATTED — the backrest is SEPARATE horizontal bars with visible gaps between them.
    ///   CURVED  — every part is round; the backrest is a curved cylindrical surface.
    ///
    /// They partition cleanly: "is the back round?" separates CURVED, and "does the back have
    /// gaps?" separates SLATTED from SOLID. No aesthetic judgement is involved at any step.
    ///
    /// ORDER IS DELIBERATELY UNCHANGED. Unity serialises enums by integer, and the seeded
    /// generator draws shapes by index, so Solid=0, Slatted=1, Curved=2 keeps every existing
    /// asset value and every seed pointing at exactly the same geometry as before. Only the
    /// NAME changed — see the CSV compatibility note in the data README.
    /// </summary>
    public enum ChairShape
    {
        /// <summary>One continuous flat back panel. Was "Modern".</summary>
        Solid,

        /// <summary>Separate horizontal back bars with gaps between them. Was "Classic".</summary>
        Slatted,

        /// <summary>Round, cylindrical parts and a curved back. Was "Rounded".</summary>
        Curved,
    }

    /// <summary>
    /// A (colour, size, shape) triple. Used both for a chair's own attributes and for the
    /// target specification the participant is asked to find, so correctness is a single
    /// value comparison rather than per-chair hard-coded logic.
    /// </summary>
    [Serializable]
    public struct ChairSpec : IEquatable<ChairSpec>
    {
        public ChairColor color;
        public ChairSize size;
        public ChairShape shape;

        public ChairSpec(ChairColor color, ChairSize size, ChairShape shape)
        {
            this.color = color;
            this.size = size;
            this.shape = shape;
        }

        public bool Matches(ChairSpec other)
        {
            return color == other.color && size == other.size && shape == other.shape;
        }

        /// <summary>How many of the three attributes agree. Useful for error analysis.</summary>
        public int MatchCount(ChairSpec other)
        {
            var n = 0;
            if (color == other.color) n++;
            if (size == other.size) n++;
            if (shape == other.shape) n++;
            return n;
        }

        /// <summary>e.g. "LARGE, BLUE, MODERN" — compact single-line form, used in logs.</summary>
        public string ToInstructionString()
        {
            return $"{size.ToString().ToUpperInvariant()}, " +
                   $"{color.ToString().ToUpperInvariant()}, " +
                   $"{shape.ToString().ToUpperInvariant()}";
        }

        /// <summary>
        /// The three attributes on three lines, colour first, for the target panel.
        ///
        /// One attribute per line rather than a comma-separated sentence: the participant has
        /// to hold all three in mind while searching, and a stacked list is read as three
        /// separate items instead of one phrase. The response timer is running while this is
        /// being read, so it is worth the vertical space.
        /// </summary>
        public string ToTargetLines()
        {
            return $"{color.ToString().ToUpperInvariant()}\n" +
                   $"{size.ToString().ToUpperInvariant()}\n" +
                   $"{shape.ToString().ToUpperInvariant()}";
        }

        public override string ToString() => $"{size}/{color}/{shape}";

        public bool Equals(ChairSpec other) => Matches(other);

        public override bool Equals(object obj) => obj is ChairSpec other && Matches(other);

        public override int GetHashCode()
        {
            return ((int)color * 397) ^ ((int)size * 31) ^ (int)shape;
        }
    }

    /// <summary>
    /// Maps the enums to concrete presentation values. Kept in one place so that changing
    /// what "Large" or "Blue" looks like never means editing six chairs by hand.
    /// </summary>
    public static class ChairAttributeVisuals
    {
        public static Color ToUnityColor(ChairColor color)
        {
            switch (color)
            {
                case ChairColor.Red: return new Color(0.78f, 0.12f, 0.12f);
                case ChairColor.Blue: return new Color(0.12f, 0.33f, 0.80f);
                case ChairColor.Green: return new Color(0.13f, 0.60f, 0.24f);
                case ChairColor.Yellow: return new Color(0.92f, 0.78f, 0.13f);
                case ChairColor.White: return new Color(0.90f, 0.90f, 0.90f);
                case ChairColor.Black: return new Color(0.10f, 0.10f, 0.11f);
                default: return Color.magenta;
            }
        }

        /// <summary>Uniform scale multiplier applied to the whole chair.</summary>
        public static float ToScale(ChairSize size)
        {
            switch (size)
            {
                case ChairSize.Small: return 0.72f;
                case ChairSize.Medium: return 1.0f;
                case ChairSize.Large: return 1.35f;
                default: return 1f;
            }
        }
    }
}
