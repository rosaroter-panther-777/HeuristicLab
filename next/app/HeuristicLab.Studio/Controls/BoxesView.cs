using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using HeuristicLab.Next.Runtime.Visuals;

namespace HeuristicLab.Studio.Controls;

/// <summary>
/// 3D view of boxes in a container (3D bin packing), like HeuristicLab's packing view: the
/// container as a wireframe, items as shaded solid boxes. Drag to rotate, wheel to zoom,
/// double-click to reset. Faces are drawn back to front (painter's algorithm) after removing those
/// facing away; the rotation is kept while the packing is updated live.
/// </summary>
public class BoxesView : Control {
  public static readonly StyledProperty<BoxesVisual?> BoxesProperty =
    AvaloniaProperty.Register<BoxesView, BoxesVisual?>(nameof(Boxes));

  public static readonly StyledProperty<IBrush?> ForegroundProperty =
    AvaloniaProperty.Register<BoxesView, IBrush?>(nameof(Foreground), Brushes.Gray);

  static BoxesView() {
    AffectsRender<BoxesView>(BoxesProperty, ForegroundProperty);
  }

  public BoxesVisual? Boxes { get => GetValue(BoxesProperty); set => SetValue(BoxesProperty, value); }
  public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

  private const double DefaultYaw = -35, DefaultPitch = 25;
  private double yaw = DefaultYaw, pitch = DefaultPitch, zoom = 1;
  private Point? dragStart;
  private (double Yaw, double Pitch) atDragStart;

  /// <summary>Rotation in degrees (around the vertical axis, then tilting towards the viewer).</summary>
  public (double Yaw, double Pitch) Rotation => (yaw, pitch);

  public BoxesView() => ClipToBounds = true;

  protected override void OnPointerPressed(PointerPressedEventArgs e) {
    base.OnPointerPressed(e);
    if (e.ClickCount == 2) { yaw = DefaultYaw; pitch = DefaultPitch; zoom = 1; InvalidateVisual(); return; }
    dragStart = e.GetPosition(this);
    atDragStart = (yaw, pitch);
    e.Pointer.Capture(this);
  }

  protected override void OnPointerMoved(PointerEventArgs e) {
    base.OnPointerMoved(e);
    if (dragStart is not { } start) return;
    var delta = e.GetPosition(this) - start;
    yaw = atDragStart.Yaw - delta.X * 0.5;
    pitch = Math.Clamp(atDragStart.Pitch + delta.Y * 0.5, -89, 89);
    InvalidateVisual();
  }

  protected override void OnPointerReleased(PointerReleasedEventArgs e) {
    base.OnPointerReleased(e);
    dragStart = null;
    e.Pointer.Capture(null);
  }

  protected override void OnPointerWheelChanged(PointerWheelEventArgs e) {
    base.OnPointerWheelChanged(e);
    zoom = Math.Clamp(zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), 0.3, 10);
    InvalidateVisual();
    e.Handled = true;
  }

  private readonly record struct Vec(double X, double Y, double Z) {
    public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
  }

  private sealed record Face(Point[] Corners, double Depth, Color Fill, bool Container);

  // corners of a unit box and its faces (indices), each with its outward normal
  private static readonly Vec[] Unit = [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0), new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1)];
  private static readonly (int[] Corners, Vec Normal)[] Faces = [
    ([0, 1, 2, 3], new(0, 0, -1)), ([4, 5, 6, 7], new(0, 0, 1)), ([0, 1, 5, 4], new(0, -1, 0)),
    ([3, 2, 6, 7], new(0, 1, 0)), ([0, 3, 7, 4], new(-1, 0, 0)), ([1, 2, 6, 5], new(1, 0, 0))
  ];

  public override void Render(DrawingContext context) {
    context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
    var foreground = Foreground ?? Brushes.Gray;
    var visual = Boxes;
    if (visual == null) return;
    var container = visual.Container;
    var center = new Vec(container.X + container.Width / 2, container.Y + container.Height / 2, container.Z + container.Depth / 2);
    double radius = Math.Sqrt(container.Width * container.Width + container.Height * container.Height + container.Depth * container.Depth) / 2;
    double scale = Math.Max(1e-9, Math.Min(Bounds.Width, Bounds.Height) / 2 / Math.Max(radius, 1e-9) * 0.92 * zoom);
    double cosYaw = Math.Cos(yaw * Math.PI / 180), sinYaw = Math.Sin(yaw * Math.PI / 180);
    double cosPitch = Math.Cos(pitch * Math.PI / 180), sinPitch = Math.Sin(pitch * Math.PI / 180);

    // view space: x right, y up, z towards the viewer
    Vec Rotate(Vec v) {
      double x = v.X * cosYaw + v.Z * sinYaw, z = -v.X * sinYaw + v.Z * cosYaw;
      double y = v.Y * cosPitch - z * sinPitch;
      z = v.Y * sinPitch + z * cosPitch;
      return new Vec(x, y, z);
    }
    Point Project(Vec v) => new(Bounds.Width / 2 + v.X * scale, Bounds.Height / 2 - v.Y * scale);
    var light = new Vec(0.35, 0.8, 0.5);

    var faces = new List<Face>();
    void AddBox(Box3 box, bool isContainer) {
      var corners = Unit.Select(u => Rotate(new Vec(box.X + u.X * box.Width, box.Y + u.Y * box.Height, box.Z + u.Z * box.Depth) - center)).ToArray();
      foreach (var (indices, normal) in Faces) {
        var n = Rotate(normal);
        bool facing = n.Z > 0;
        // the container shows its inside: only the faces turned away (back walls, floor)
        if (facing == isContainer) continue;
        double shade = isContainer ? 1 : 0.55 + 0.45 * Math.Max(0, (n.X * light.X + n.Y * light.Y + n.Z * light.Z) / 1.03);
        var c = box.Color;
        var fill = Color.FromArgb(isContainer ? (byte)40 : c.A, (byte)(c.R * shade), (byte)(c.G * shade), (byte)(c.B * shade));
        faces.Add(new Face(indices.Select(i => Project(corners[i])).ToArray(), indices.Average(i => corners[i].Z), fill, isContainer));
      }
    }
    AddBox(container, true);
    foreach (var box in visual.Boxes) AddBox(box, false);

    var edge = new Pen(new SolidColorBrush(Color.FromArgb(150, 30, 30, 30)), 0.8);
    foreach (var face in faces.OrderBy(f => f.Container ? double.MinValue : f.Depth)) {
      var geometry = new StreamGeometry();
      using (var g = geometry.Open()) {
        g.BeginFigure(face.Corners[0], true);
        foreach (var p in face.Corners.Skip(1)) g.LineTo(p);
        g.EndFigure(true);
      }
      context.DrawGeometry(new SolidColorBrush(face.Fill), face.Container ? null : edge, geometry);
    }

    // container edges on top, so its outline stays visible
    var containerCorners = Unit.Select(u => Project(Rotate(new Vec(container.X + u.X * container.Width, container.Y + u.Y * container.Height,
      container.Z + u.Z * container.Depth) - center))).ToArray();
    var outline = new Pen(foreground, 1.2);
    int[][] edges = [[0, 1], [1, 2], [2, 3], [3, 0], [4, 5], [5, 6], [6, 7], [7, 4], [0, 4], [1, 5], [2, 6], [3, 7]];
    foreach (var e in edges) context.DrawLine(outline, containerCorners[e[0]], containerCorners[e[1]]);

    var hint = new FormattedText("Drag to rotate, scroll to zoom, double-click to reset", CultureInfo.InvariantCulture,
      FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 11, foreground);
    context.DrawText(hint, new Point(8, Bounds.Height - hint.Height - 6));
  }
}
