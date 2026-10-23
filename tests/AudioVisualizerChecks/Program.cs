using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Attributes;
using AudioVisualizer.Locales;
using AudioVisualizer.Models;
using AudioVisualizer.Services;
using AudioVisualizer.Views;
using AudioVisualizer.Views.Settings;

AppBuilder.Configure<Application>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSkia()
    .SetupWithoutStarting();

if (Application.Current != null && Application.Current.Styles.Count == 0)
{
    Application.Current.Styles.Add(new FluentTheme());
}

int passed = 0;
int failed = 0;

void Check(bool condition, string name)
{
    if (condition)
    {
        Console.WriteLine($"[PASS] {name}");
        passed++;
    }
    else
    {
        Console.WriteLine($"[FAIL] {name}");
        failed++;
    }
}

Console.WriteLine("==================================================");
Console.WriteLine("Running AudioVisualizer Component Verification Suite");
Console.WriteLine("==================================================");

// 1. Assembly & Widget Registration Verification
var asm = typeof(AudioVisualizerView).Assembly;
var widgetAttr = asm.GetCustomAttribute<WidgetInfoAttribute>();
Check(widgetAttr != null, "WidgetInfoAttribute is registered on assembly");
Check(widgetAttr?.ViewType == typeof(AudioVisualizerView), "WidgetInfo ViewType is AudioVisualizerView");
Check(widgetAttr?.ModelType == typeof(AudioVisualizerModel), "WidgetInfo ModelType is AudioVisualizerModel");
Check(widgetAttr?.EditModelViewType == typeof(AudioVisualizerSettings), "WidgetInfo EditModelViewType is AudioVisualizerSettings");
Check(widgetAttr?.Category == "WidgetCategory_Lifestyle", "WidgetInfo Category is WidgetCategory_Lifestyle");

var localeAttr = asm.GetCustomAttribute<LocaleAttribute>();
Check(localeAttr != null, "LocaleAttribute is registered on assembly");
Check(localeAttr?.LocaleType == typeof(Locale), "Locale resource type matches Locale");
Check(!string.IsNullOrEmpty(localeAttr?.IconData), "Locale SVG rhythm icon is non-empty");

// 2. Model Normalization & Serialization
var model = new AudioVisualizerModel();
Check(!model.IsFrameless, "Default model is not frameless");
Check(model.AudioSource == AudioSourceType.SystemAudio, "Default audio source is SystemAudio (Loopback)");
Check(model.VisualizerStyle == VisualizerStyle.Bars, "Default visualizer style is Bars");
Check(model.RhythmDirection == RhythmDirection.Unidirectional, "Default rhythm direction is Unidirectional");

var clamped = (model with { BarCount = 999, StrokeWidth = 100, Sensitivity = 99 }).Normalize();
Check(clamped.BarCount == 128, "BarCount clamped to max 128");
Check(clamped.StrokeWidth == 20.0, "StrokeWidth clamped to max 20.0");
Check(clamped.Sensitivity == 5.0, "Sensitivity clamped to max 5.0");

string json = JsonSerializer.Serialize(model);
var deserialized = JsonSerializer.Deserialize<AudioVisualizerModel>(json);
Check(deserialized != null && deserialized.Points != null, "Model JSON roundtrip succeeds");

// 3. Preset Shapes Verification
var (hlPoints, hlClosed, hlMode) = PresetShapes.CreateHorizontalLine();
Check(hlPoints.Count >= 2 && !hlClosed && hlMode == DrawToolMode.Line, "Horizontal line preset valid");

var (circlePoints, circleClosed, circleMode) = PresetShapes.CreateCircle();
Check(circlePoints.Count >= 16 && circleClosed && circleMode == DrawToolMode.Curve, "Circle closed shape preset valid");

var (heartPoints, heartClosed, _) = PresetShapes.CreateHeart();
Check(heartPoints.Count >= 16 && heartClosed, "Heart closed shape preset valid");

foreach (var pt in circlePoints)
{
    Check(pt.X >= 0.0 && pt.X <= 1.0 && pt.Y >= 0.0 && pt.Y <= 1.0, $"Preset point in bounds [0, 1]: ({pt.X}, {pt.Y})");
}

// 4. Path Geometry & Equidistant Normal Calculation
Size testSizeSmall = new(180, 90);
Size testSizeLarge = new(720, 360);

var smallSamples = PathGeometryHelper.SamplePath(hlPoints, hlClosed, hlMode, testSizeSmall, sampleCount: 24);
Check(smallSamples.Count == 24, "SamplePath generates exact requested sample count on small size (180x90)");

var largeSamples = PathGeometryHelper.SamplePath(circlePoints, circleClosed, circleMode, testSizeLarge, sampleCount: 32);
Check(largeSamples.Count == 32, "SamplePath generates exact requested sample count on large size (720x360)");

// Check circle normals point outwards
Point centroid = new(360, 180);
bool allOutward = true;
foreach (var sp in largeSamples)
{
    double dOut = (sp.Position.X + sp.Normal.X - centroid.X) * (sp.Position.X + sp.Normal.X - centroid.X) +
                  (sp.Position.Y + sp.Normal.Y - centroid.Y) * (sp.Position.Y + sp.Normal.Y - centroid.Y);
    double dIn = (sp.Position.X - sp.Normal.X - centroid.X) * (sp.Position.X - sp.Normal.X - centroid.X) +
                 (sp.Position.Y - sp.Normal.Y - centroid.Y) * (sp.Position.Y - sp.Normal.Y - centroid.Y);
    if (dOut <= dIn) allOutward = false;
}
Check(allOutward, "All normal vectors of closed circular shape point outwards radially");

// 5. Unidirectional vs Bidirectional Bar Generation
float[] testMags = new float[24];
for (int i = 0; i < testMags.Length; i++) testMags[i] = 0.5f;

var uniBars = PathGeometryHelper.BuildBars(smallSamples, testMags, RhythmDirection.Unidirectional, maxAmplitude: 40);
var biBars = PathGeometryHelper.BuildBars(smallSamples, testMags, RhythmDirection.Bidirectional, maxAmplitude: 40);
Check(uniBars.Count == 24 && biBars.Count == 24, "Bars built for both Unidirectional and Bidirectional");

// In Unidirectional, start is exactly at path point:
Check(Math.Abs(uniBars[0].Start.X - smallSamples[0].Position.X) < 0.001 &&
      Math.Abs(uniBars[0].Start.Y - smallSamples[0].Position.Y) < 0.001,
      "Unidirectional bar starts on baseline path");

// In Bidirectional, start and end extend equally on both sides:
double midX = (biBars[0].Start.X + biBars[0].End.X) * 0.5;
double midY = (biBars[0].Start.Y + biBars[0].End.Y) * 0.5;
Check(Math.Abs(midX - smallSamples[0].Position.X) < 0.001 &&
      Math.Abs(midY - smallSamples[0].Position.Y) < 0.001,
      "Bidirectional bar is centered symmetrically around baseline path");

// 6. FFT Spectrum Analysis
float[] sineSamples = new float[FftCalculator.FftSize];
for (int i = 0; i < sineSamples.Length; i++)
{
    // 440 Hz tone at 48000 Hz sample rate
    sineSamples[i] = MathF.Sin(2.0f * MathF.PI * 440.0f * i / 48000.0f);
}
float[] spectrum = new float[32];
FftCalculator.ComputeSpectrum(sineSamples, spectrum);

float maxVal = 0f;
int peakBand = -1;
for (int i = 0; i < spectrum.Length; i++)
{
    if (spectrum[i] > maxVal)
    {
        maxVal = spectrum[i];
        peakBand = i;
    }
}
Check(maxVal > 0.05f && peakBand >= 3 && peakBand <= 15, $"FFT correctly detects tone peak at band {peakBand} with amplitude {maxVal:F3}");

// 7. AudioCaptureService LifeCycle & Source Switching
AudioCaptureService.Instance.Retain(AudioSourceType.SystemAudio);
AudioCaptureService.Instance.SwitchSource(AudioSourceType.Microphone);
AudioCaptureService.Instance.SwitchSource(AudioSourceType.SystemAudio);

float[] readBands = new float[32];
AudioCaptureService.Instance.ReadSpectrum(readBands, sensitivity: 1.0, smoothing: 0.8, forceSimulate: true);
Check(readBands[0] > 0.001f, "ReadSpectrum returns active rhythm values (forceSimulate is true)");

float[] silentBands = new float[32];
AudioCaptureService.Instance.ReadSpectrum(silentBands, sensitivity: 1.0, smoothing: 0.8, forceSimulate: false);
Check(silentBands[0] == 0.0f, "ReadSpectrum is quiet (0.0f) when no audio is playing and forceSimulate is false");
AudioCaptureService.Instance.Release();

// 8. View Contracts & Frameless Interface
var view = new AudioVisualizerView();
Check(view is IFramelessWidget, "AudioVisualizerView implements IFramelessWidget");
Check(view is IWidgetSuspendable, "AudioVisualizerView implements IWidgetSuspendable");
Check(!view.IsFrameless, "View IsFrameless matches default model (false)");

var framelessModel = new AudioVisualizerModel { IsFrameless = true };
var framelessView = new AudioVisualizerView(null, framelessModel);
Check(framelessView.IsFrameless, "View IsFrameless matches frameless model (true)");

view.Suspend();
view.Resume();
view.Dispose();
framelessView.Dispose();
Check(true, "Suspend, Resume, and Dispose lifecycle methods execute cleanly without throwing");

Console.WriteLine("==================================================");
Console.WriteLine($"Verification Complete: {passed} PASSED, {failed} FAILED");
Console.WriteLine("==================================================");

return failed == 0 ? 0 : 1;
