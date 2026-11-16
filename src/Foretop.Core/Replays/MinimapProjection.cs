using System.Globalization;
using System.Xml.Linq;

namespace Foretop.Core.Replays;

/// <summary>
/// The playable area of a map in world units (X east, Z north), which the minimap images cover exactly.
/// <see cref="Known"/> is false when the map's <c>space.settings</c> could not be read and a default is used.
/// </summary>
public readonly record struct MapBounds(double MinX, double MaxX, double MinZ, double MaxZ, bool Known = true)
{
    /// <summary>Used when a map's bounds cannot be read: the most common size (±700).</summary>
    public static MapBounds Default => new(-700, 700, -700, 700, Known: false);

    public double Width => MaxX - MinX;
    public double Height => MaxZ - MinZ;
    public double CenterX => (MinX + MaxX) / 2;
    public double CenterZ => (MinZ + MaxZ) / 2;

    /// <summary>
    /// Reads the chunk bounds of <c>space.settings</c>. The space covers chunks minX…maxX (each <c>chunkSize</c>
    /// units, 100 when not given), and two chunks on every side lie outside the playable area. The bounds are written
    /// either as child elements (<c>&lt;minX&gt;-9&lt;/minX&gt;</c>) or as attributes (<c>&lt;bounds minX="-9" … /&gt;</c>);
    /// most maps use attributes. Y in the file is the world Z axis.
    /// </summary>
    public static MapBounds FromSpaceSettings(string? spaceSettings)
    {
        if (string.IsNullOrWhiteSpace(spaceSettings)) return Default;
        try
        {
            var root = XDocument.Parse(spaceSettings).Root!;
            var bounds = root.Element("bounds");
            if (bounds is null) return Default;
            double? Read(XElement parent, string name)
            {
                var text = parent.Attribute(name)?.Value ?? parent.Element(name)?.Value;
                return double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            }
            var chunk = Read(root, "chunkSize") is double c && c > 0 ? c : 100;
            if (Read(bounds, "minX") is not double minX || Read(bounds, "maxX") is not double maxX) return Default;
            var minY = Read(bounds, "minY") ?? minX;
            var maxY = Read(bounds, "maxY") ?? maxX;
            // Chunk i spans i·chunk … (i+1)·chunk; the playable area leaves out two chunks on each side.
            const int border = 2;
            var b = new MapBounds((minX + border) * chunk, (maxX + 1 - border) * chunk, (minY + border) * chunk, (maxY + 1 - border) * chunk);
            if (b.Width <= 0 || b.Height <= 0)
                b = new MapBounds(minX * chunk, (maxX + 1) * chunk, minY * chunk, (maxY + 1) * chunk); // tiny spaces (ports): no border
            return b.Width > 0 && b.Height > 0 ? b : Default;
        }
        catch (System.Xml.XmlException)
        {
            return Default;
        }
    }
}

/// <summary>
/// The one transformation between world coordinates and the screen for the replay minimap: the map is fitted into
/// the view without distortion (aspect ratio kept), centred, then zoomed around the camera centre. Everything the
/// minimap draws (images, grid, capture points, smoke, trails, ships) is placed through this class.
/// </summary>
/// <remarks>
/// The camera is stored as the map point at the centre of the view, in map fractions (0…1, u east, v south), and the
/// zoom is relative to the fitted size, so resizing the window keeps the same part of the map in view.
/// </remarks>
public sealed class MinimapProjection
{
    public const double MinZoom = 1;
    public const double MaxZoom = 8;

    public MinimapProjection(MapBounds bounds, double viewLeft, double viewTop, double viewWidth, double viewHeight,
        double zoom = 1, double cameraU = 0.5, double cameraV = 0.5)
    {
        Bounds = bounds;
        View = (viewLeft, viewTop, Math.Max(0, viewWidth), Math.Max(0, viewHeight));
        Zoom = Math.Clamp(double.IsFinite(zoom) ? zoom : 1, MinZoom, MaxZoom);
        FitScale = bounds.Width > 0 && bounds.Height > 0 ? Math.Min(View.Width / bounds.Width, View.Height / bounds.Height) : 0;
        Scale = FitScale * Zoom;
        (CameraU, CameraV) = Clamp(cameraU, cameraV);
    }

    public MapBounds Bounds { get; }
    public (double Left, double Top, double Width, double Height) View { get; }
    public double Zoom { get; }

    /// <summary>Screen units per world unit with the whole map fitted (zoom 1).</summary>
    public double FitScale { get; }

    /// <summary>Screen units per world unit at the current zoom.</summary>
    public double Scale { get; }

    public double CameraU { get; }
    public double CameraV { get; }
    public bool IsValid => Scale > 0;

    private double CenterX => View.Left + View.Width / 2;
    private double CenterY => View.Top + View.Height / 2;

    /// <summary>The whole map on screen (may extend beyond the view when zoomed in).</summary>
    public (double Left, double Top, double Width, double Height) MapRect
    {
        get
        {
            var w = Bounds.Width * Scale;
            var h = Bounds.Height * Scale;
            return (CenterX - CameraU * w, CenterY - CameraV * h, w, h);
        }
    }

    public (double X, double Y) ToScreen(double x, double z)
    {
        var (u, v) = ToMap(x, z);
        var r = MapRect;
        return (r.Left + u * r.Width, r.Top + v * r.Height);
    }

    public (double X, double Z) ToWorld(double sx, double sy)
    {
        var r = MapRect;
        var u = r.Width > 0 ? (sx - r.Left) / r.Width : 0.5;
        var v = r.Height > 0 ? (sy - r.Top) / r.Height : 0.5;
        return (Bounds.MinX + u * Bounds.Width, Bounds.MaxZ - v * Bounds.Height);
    }

    /// <summary>Map fractions: u 0 = west edge, 1 = east; v 0 = north edge, 1 = south.</summary>
    public (double U, double V) ToMap(double x, double z) =>
        ((x - Bounds.MinX) / Bounds.Width, (Bounds.MaxZ - z) / Bounds.Height);

    /// <summary>A distance in world units (radius, length) on screen.</summary>
    public double Length(double worldUnits) => worldUnits * Scale;

    /// <summary>The same view with a new zoom, keeping the map point under the screen point (sx, sy) in place.</summary>
    public MinimapProjection ZoomAt(double newZoom, double sx, double sy)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        var (x, z) = ToWorld(sx, sy);
        var (u, v) = ToMap(x, z);
        var scale = FitScale * newZoom;
        var camU = Bounds.Width * scale > 0 ? u - (sx - CenterX) / (Bounds.Width * scale) : 0.5;
        var camV = Bounds.Height * scale > 0 ? v - (sy - CenterY) / (Bounds.Height * scale) : 0.5;
        return With(newZoom, camU, camV);
    }

    /// <summary>The same view moved by a screen distance (dragging the map by dx, dy).</summary>
    public MinimapProjection Pan(double dx, double dy)
    {
        var r = MapRect;
        return r.Width <= 0 || r.Height <= 0 ? this : With(Zoom, CameraU - dx / r.Width, CameraV - dy / r.Height);
    }

    public MinimapProjection With(double zoom, double cameraU, double cameraV) =>
        new(Bounds, View.Left, View.Top, View.Width, View.Height, zoom, cameraU, cameraV);

    /// <summary>Keeps the map filling the view when zoomed in, and centred where it is smaller than the view.</summary>
    private (double U, double V) Clamp(double u, double v)
    {
        double One(double c, double viewExtent, double mapExtent)
        {
            if (!double.IsFinite(c) || mapExtent <= 0) return 0.5;
            var half = viewExtent / (2 * mapExtent); // half the view, in map fractions
            return half >= 0.5 ? 0.5 : Math.Clamp(c, half, 1 - half);
        }
        return (One(u, View.Width, Bounds.Width * Scale), One(v, View.Height, Bounds.Height * Scale));
    }
}
