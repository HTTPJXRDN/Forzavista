using System.Diagnostics;

namespace ForzavistaFreeRoam;

public partial class MainWindow
{
    private readonly Stopwatch _lightingFrameClock = Stopwatch.StartNew();
    private readonly LightingFrameTiming _lightingFrameTiming = new();

    private async Task UpdateLightingAnimationAsync()
    {
        bool fade = _lightStatus.Fading;
        bool colors = _drlColorPreparation is null &&
            _drlColorStatus.Mode != DrlColorMode.Original && !_drlColorStatus.NeedsReset;
        if (_closing || _updatingDrlColor) return;
        if (!fade && !colors) { LogLightingFrameTiming(); return; }
        if (!_actionGate.TryEnterBackground()) return;
        _updatingDrlColor = true;
        try
        {
            double period = _drlRainbowPeriodSeconds, brightness = _drlBrightnessPercent, speed = _drlStrobeFlashesPerSecond;
            var (result, colorStatus, pending, workMs) = await Task.Run(() =>
            {
                var work = Stopwatch.StartNew();
                NativeLightActionResult? result = fade
                    ? NativeCarControl.UpdateLightFades(() => _actionGate.IsManualWaiting) : null;
                NativeDrlColorStatus? status = null;
                // Fade commits its gain first; one color update then applies it to
                // native49 and advances RGB/custom strobe, in the same gate slot.
                if (colors && !_actionGate.IsManualWaiting)
                {
                    NativeCarControl.SetDrlRainbowPeriod(period);
                    NativeCarControl.SetDrlBrightness(brightness);
                    NativeCarControl.SetDrlStrobeSpeed(speed);
                    status = NativeCarControl.UpdateDrlColor(() => _actionGate.IsManualWaiting);
                }
                return (result, status, fade && NativeCarControl.HasLightFade, work.Elapsed.TotalMilliseconds);
            });
            _lightingFrameTiming.Record(_lightingFrameClock.Elapsed.TotalSeconds, workMs);
            if (fade)
            {
                var state = result?.LightStatus ?? (_lightStatus with { Fading = pending });
                if (state != _lightStatus) { _lightStatus = state; RefreshLightLabels(); }
                if (result is { Success: false } && !_closing) SetControlsStatus(result.Message, Warn);
            }
            if (colorStatus is not null && colorStatus != _drlColorStatus)
            {
                _drlColorStatus = colorStatus;
                if (!_closing) RefreshDrlColorControls();
                if (!_closing && colorStatus.NeedsReset) SetControlsStatus(colorStatus.Message, Warn);
            }
            RefreshDrlColorTimerInterval();
            if (_lightingFrameTiming.Due(_lightingFrameClock.Elapsed.TotalSeconds) ||
                !pending && (_drlColorStatus.Mode == DrlColorMode.Original || _drlColorStatus.NeedsReset))
                LogLightingFrameTiming();
        }
        catch (Exception error)
        {
            SessionLog.Write("lighting_animation_error", error.ToString());
            if (!_closing) SetControlsStatus(error.Message, Warn);
        }
        finally { _updatingDrlColor = false; _actionGate.Release(); }
    }

    private void LogLightingFrameTiming()
    {
        if (_lightingFrameTiming.Finish() is { } metrics)
            SessionLog.Write("lighting_animation_timing", metrics, gamePid: _sessionProcessId, carToken: _sessionCarToken);
    }
}
