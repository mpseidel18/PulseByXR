using System;
using Meta.XR;
using Meta.XR.ImmersiveDebugger;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

public class PulseMonitor : MonoBehaviour
{
    [Header("Camera Source")]
    [Tooltip("Same PassthroughCameraAccess reference used by GetCameraFeed on this GameObject.")]
    public PassthroughCameraAccess cameraAccess;

    [Header("Region Of Interest (fixed center crop)")]
    [Range(0.05f, 0.6f)] public float roiWidthPercent = 0.18f;
    [Range(0.05f, 0.6f)] public float roiHeightPercent = 0.18f;

    [Header("Signal Buffer")]
    [Tooltip("Seconds of ROI samples retained for analysis.")]
    public float bufferDurationSeconds = 15f;
    [Tooltip("Minimum seconds of buffered data required before attempting an estimate.")]
    public float minAnalysisWindowSeconds = 8f;
    [Tooltip("Assumed max camera sample rate, used only to size the internal ring buffer.")]
    public float assumedMaxSampleRateHz = 90f;

    [Header("Analysis")]
    [Tooltip("How often to run the BPM estimate + log, independent of camera frame rate.")]
    public float analysisIntervalSeconds = 1f;
    [Tooltip("Uniform grid rate the irregular ROI samples are resampled onto before Goertzel.")]
    public float resampleRateHz = 30f;
    public float minBpm = 42f;
    public float maxBpm = 180f;
    public float bpmStepSize = 1f;

    [Header("Confidence Gate")]
    [DebugMember(Tweakable = true, Min = 1f, Max = 15f, Category = "PulseMonitor")]
    public float confidenceThreshold = 4f;

    [Header("Debug Output (Immersive Debugger + Console)")]
    [DebugMember(Category = "PulseMonitor")] private string _bpmDisplay = "-";
    [DebugMember(Category = "PulseMonitor")] private float _lastConfidence;
    [DebugMember(Category = "PulseMonitor")] private int _bufferedSampleCount;
    [DebugMember(Category = "PulseMonitor")] private float _bufferedSpanSeconds;

    // Ring buffer of (elapsed seconds, mean green) ROI samples.
    private double[] _sampleTimes;
    private float[] _sampleValues;
    private int _head;
    private int _count;
    private double? _firstTimestampSeconds;

    // Reused scratch buffer for the uniformly-resampled analysis window.
    private float[] _resampleScratch;

    private Vector2Int _cachedResolution;
    private RectInt _roiPixelRect;
    private bool _readbackPending;

    private float _analysisTimer;

    void Awake()
    {
        if (cameraAccess == null)
        {
            Debug.LogError("PulseMonitor: No PassthroughCameraAccess assigned.");
            enabled = false;
            return;
        }

        int capacity = Mathf.CeilToInt((bufferDurationSeconds + 2f) * assumedMaxSampleRateHz);
        _sampleTimes = new double[capacity];
        _sampleValues = new float[capacity];

        int scratchLength = Mathf.CeilToInt(bufferDurationSeconds * resampleRateHz) + 4;
        _resampleScratch = new float[scratchLength];
    }

    void Update()
    {
        if (cameraAccess == null || !cameraAccess.IsPlaying)
            return;

        UpdateRoiRectIfNeeded();
        TryIssueReadback();

        _analysisTimer += Time.deltaTime;
        if (_analysisTimer >= analysisIntervalSeconds)
        {
            _analysisTimer = 0f;
            RunAnalysis();
        }
    }

    private void UpdateRoiRectIfNeeded()
    {
        var res = cameraAccess.CurrentResolution;
        if (res == _cachedResolution)
            return;

        _cachedResolution = res;
        int w = Mathf.Max(2, Mathf.RoundToInt(res.x * roiWidthPercent));
        int h = Mathf.Max(2, Mathf.RoundToInt(res.y * roiHeightPercent));
        int x = (res.x - w) / 2;
        int y = (res.y - h) / 2;
        _roiPixelRect = new RectInt(x, y, w, h);
    }

    private void TryIssueReadback()
    {
        if (_readbackPending || !cameraAccess.IsUpdatedThisFrame)
            return;

        var tex = cameraAccess.GetTexture();
        if (tex == null)
            return;

        var capturedTimestamp = cameraAccess.Timestamp;
        _readbackPending = true;

        AsyncGPUReadback.Request(
            tex, 0,
            _roiPixelRect.x, _roiPixelRect.width,
            _roiPixelRect.y, _roiPixelRect.height,
            0, 1,
            GraphicsFormat.R8G8B8A8_UNorm,
            request => OnReadbackComplete(request, capturedTimestamp));
    }

    private void OnReadbackComplete(AsyncGPUReadbackRequest request, DateTime timestamp)
    {
        _readbackPending = false;
        if (this == null)
            return;

        if (request.hasError)
        {
            Debug.LogWarning("PulseMonitor: AsyncGPUReadback error on ROI request, dropping this sample.");
            return;
        }

        NativeArray<Color32> data = request.GetData<Color32>();
        long sumGreen = 0;
        int n = data.Length;
        for (int i = 0; i < n; i++)
            sumGreen += data[i].g;
        float meanGreen = n > 0 ? (float)sumGreen / n : 0f;

        PushSample(SecondsSince(timestamp), meanGreen);
    }

    private double SecondsSince(DateTime timestamp)
    {
        double absoluteSeconds = timestamp.Subtract(DateTime.UnixEpoch).TotalSeconds;
        if (!_firstTimestampSeconds.HasValue)
            _firstTimestampSeconds = absoluteSeconds;
        return absoluteSeconds - _firstTimestampSeconds.Value;
    }

    private void PushSample(double t, float v)
    {
        int capacity = _sampleTimes.Length;
        if (_count < capacity)
        {
            int writeIndex = (_head + _count) % capacity;
            _sampleTimes[writeIndex] = t;
            _sampleValues[writeIndex] = v;
            _count++;
        }
        else
        {
            _sampleTimes[_head] = t;
            _sampleValues[_head] = v;
            _head = (_head + 1) % capacity;
        }

        while (_count > 1 && (t - _sampleTimes[_head]) > bufferDurationSeconds)
        {
            _head = (_head + 1) % capacity;
            _count--;
        }
    }

    private double SampleTimeAt(int i) => _sampleTimes[(_head + i) % _sampleTimes.Length];
    private float SampleValueAt(int i) => _sampleValues[(_head + i) % _sampleValues.Length];

    private void RunAnalysis()
    {
        _bufferedSampleCount = _count;

        if (_count < 2)
        {
            _bufferedSpanSeconds = 0f;
            _bpmDisplay = "-";
            return;
        }

        double span = SampleTimeAt(_count - 1) - SampleTimeAt(0);
        _bufferedSpanSeconds = (float)span;
        if (span < minAnalysisWindowSeconds)
        {
            _bpmDisplay = "-";
            return;
        }

        int n = ResampleUniform(_resampleScratch);
        if (n < 8)
        {
            _bpmDisplay = "-";
            return;
        }

        DetrendLinear(_resampleScratch, n);
        ApplyHannWindow(_resampleScratch, n);

        int candidateCount = Mathf.FloorToInt((maxBpm - minBpm) / bpmStepSize) + 1;
        float bestPower = float.MinValue;
        int bestIndex = -1;
        double sumPower = 0;

        for (int c = 0; c < candidateCount; c++)
        {
            float bpm = minBpm + c * bpmStepSize;
            double freqHz = bpm / 60.0;
            float power = GoertzelPower(_resampleScratch, n, freqHz, resampleRateHz);
            sumPower += power;
            if (power > bestPower)
            {
                bestPower = power;
                bestIndex = c;
            }
        }

        float meanPower = candidateCount > 0 ? (float)(sumPower / candidateCount) : 0f;
        float confidence = meanPower > 0f ? bestPower / meanPower : 0f;
        float bestBpm = minBpm + bestIndex * bpmStepSize;

        _lastConfidence = confidence;

        if (confidence >= confidenceThreshold)
        {
            _bpmDisplay = $"{bestBpm:0} BPM";
            Debug.Log($"PulseMonitor: {bestBpm:0} BPM (confidence {confidence:0.0}, span {span:0.0}s, samples {_count})");
        }
        else
        {
            _bpmDisplay = "-";
            Debug.Log($"PulseMonitor: no reliable signal (best guess {bestBpm:0} BPM, confidence {confidence:0.0} < {confidenceThreshold})");
        }
    }

    private int ResampleUniform(float[] dst)
    {
        double t0 = SampleTimeAt(0);
        double t1 = SampleTimeAt(_count - 1);
        double span = t1 - t0;
        double dt = 1.0 / resampleRateHz;
        int n = Mathf.Min(dst.Length, (int)(span / dt) + 1);

        int srcIdx = 0;
        for (int i = 0; i < n; i++)
        {
            double tg = t0 + i * dt;
            while (srcIdx < _count - 2 && SampleTimeAt(srcIdx + 1) < tg)
                srcIdx++;

            double ta = SampleTimeAt(srcIdx);
            double tb = SampleTimeAt(srcIdx + 1);
            float va = SampleValueAt(srcIdx);
            float vb = SampleValueAt(srcIdx + 1);
            double alpha = tb > ta ? (tg - ta) / (tb - ta) : 0;
            dst[i] = (float)(va + (vb - va) * alpha);
        }

        return n;
    }

    private static void DetrendLinear(float[] buf, int n)
    {
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        for (int i = 0; i < n; i++)
        {
            sumX += i;
            sumY += buf[i];
            sumXY += (double)i * buf[i];
            sumXX += (double)i * i;
        }
        double denom = n * sumXX - sumX * sumX;
        double slope = denom != 0 ? (n * sumXY - sumX * sumY) / denom : 0;
        double intercept = (sumY - slope * sumX) / n;
        for (int i = 0; i < n; i++)
            buf[i] -= (float)(slope * i + intercept);
    }

    private static void ApplyHannWindow(float[] buf, int n)
    {
        if (n < 2)
            return;
        for (int i = 0; i < n; i++)
            buf[i] *= (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (n - 1))));
    }

    private static float GoertzelPower(float[] x, int n, double targetFreqHz, double sampleRateHz)
    {
        double omega = 2.0 * Math.PI * targetFreqHz / sampleRateHz;
        double cosOmega = Math.Cos(omega);
        double coeff = 2.0 * cosOmega;

        double q1 = 0.0, q2 = 0.0;
        for (int i = 0; i < n; i++)
        {
            double q0 = coeff * q1 - q2 + x[i];
            q2 = q1;
            q1 = q0;
        }

        double real = q1 - q2 * cosOmega;
        double imag = q2 * Math.Sin(omega);
        return (float)(real * real + imag * imag);
    }
}
