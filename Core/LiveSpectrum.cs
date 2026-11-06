using System;
using DynamicIsland.Shared;
using NAudio.Dsp;
using NAudio.Wave;

namespace DynamicIsland.Core
{
    /// <summary>
    /// Живой спектр звука системы: слушаем то, что играет в колонках/наушниках
    /// (WASAPI loopback), считаем FFT и раскладываем по полосам от басов до верхов.
    /// </summary>
    public sealed class LiveSpectrum : IDisposable
    {
        private const int FftSize = 2048;
        private const int FftPower = 11; // 2^11 = 2048

        private readonly object _lock = new();
        private readonly float[] _ring = new float[FftSize];
        private readonly Complex[] _fft = new Complex[FftSize];
        private int _pos;

        private WasapiLoopbackCapture? _capture;
        private int _sampleRate = 48000, _channels = 2, _bytesPerSample = 4;
        private bool _isFloat = true;
        private DateTime _lastData = DateTime.MinValue;
        private float _peak = 1e-4f;

        public bool Running => _capture != null;

        /// <summary>Давно не было звука (тишина или поменялось устройство вывода).</summary>
        public bool Stale => DateTime.UtcNow - _lastData > TimeSpan.FromSeconds(3);

        public void Start()
        {
            if (_capture != null) return;
            try
            {
                var capture = new WasapiLoopbackCapture();
                var f = capture.WaveFormat;
                _sampleRate = f.SampleRate;
                _channels = Math.Max(1, f.Channels);
                _bytesPerSample = Math.Max(1, f.BitsPerSample / 8);
                // в общем режиме Windows почти всегда отдаёт 32-битный float
                _isFloat = f.BitsPerSample == 32 &&
                           (f.Encoding == WaveFormatEncoding.IeeeFloat || f.Encoding == WaveFormatEncoding.Extensible);

                capture.DataAvailable += OnData;
                capture.StartRecording();
                _capture = capture;
                _lastData = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Log.Warn("Живой эквалайзер не запустился: " + ex.Message);
                _capture = null;
            }
        }

        public void Stop()
        {
            var capture = _capture;
            _capture = null;
            if (capture == null) return;
            try
            {
                capture.DataAvailable -= OnData;
                capture.StopRecording();
                capture.Dispose();
            }
            catch { }
        }

        public void Restart()
        {
            Stop();
            Start();
        }

        private void OnData(object? sender, WaveInEventArgs e)
        {
            int frame = _bytesPerSample * _channels;
            if (frame <= 0 || e.BytesRecorded < frame) return;
            _lastData = DateTime.UtcNow;

            lock (_lock)
            {
                for (int i = 0; i + frame <= e.BytesRecorded; i += frame)
                {
                    float sum = 0;
                    for (int ch = 0; ch < _channels; ch++)
                        sum += ReadSample(e.Buffer, i + ch * _bytesPerSample);

                    _ring[_pos] = sum / _channels;
                    _pos = (_pos + 1) % FftSize;
                }
            }
        }

        private float ReadSample(byte[] buffer, int offset) => _bytesPerSample switch
        {
            4 when _isFloat => BitConverter.ToSingle(buffer, offset),
            4 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
            2 => BitConverter.ToInt16(buffer, offset) / 32768f,
            3 => ((buffer[offset + 2] << 24) | (buffer[offset + 1] << 16) | (buffer[offset] << 8)) / 2147483648f,
            _ => 0,
        };

        /// <summary>
        /// Заполняет <paramref name="levels"/> уровнями полос 0..1 (басы слева).
        /// Громкость нормализуется автоматически, чтобы тихие треки тоже «качали».
        /// </summary>
        public void Compute(float[] levels)
        {
            int bars = levels.Length;
            if (bars == 0) return;

            if (DateTime.UtcNow - _lastData > TimeSpan.FromMilliseconds(300))
            {
                // в тишине Windows вообще не присылает данные — опускаем столбики
                Array.Clear(levels);
                return;
            }

            lock (_lock)
            {
                for (int i = 0; i < FftSize; i++)
                {
                    float window = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / (FftSize - 1))); // окно Ханна
                    _fft[i].X = _ring[(_pos + i) % FftSize] * window;
                    _fft[i].Y = 0;
                }
            }

            FastFourierTransform.FFT(true, FftPower, _fft);

            // полосы в логарифмической шкале: 50 Гц … 14 кГц
            double fMin = 50, fMax = Math.Min(14000, _sampleRate / 2.0);
            float frameMax = 0;

            for (int b = 0; b < bars; b++)
            {
                double lo = fMin * Math.Pow(fMax / fMin, (double)b / bars);
                double hi = fMin * Math.Pow(fMax / fMin, (double)(b + 1) / bars);
                int binLo = Math.Max(1, (int)(lo * FftSize / _sampleRate));
                int binHi = Math.Min(FftSize / 2 - 1, Math.Max(binLo + 1, (int)(hi * FftSize / _sampleRate)));

                float mag = 0;
                for (int k = binLo; k <= binHi; k++)
                {
                    float m = MathF.Sqrt(_fft[k].X * _fft[k].X + _fft[k].Y * _fft[k].Y);
                    if (m > mag) mag = m;
                }

                // верхние частоты от природы тише — чуть поднимаем
                mag *= 1 + b * 0.35f;
                levels[b] = mag;
                if (mag > frameMax) frameMax = mag;
            }

            // автоусиление: пик медленно «забывается»
            _peak = Math.Max(frameMax, _peak * 0.992f);
            if (_peak < 1e-4f) _peak = 1e-4f;

            for (int b = 0; b < bars; b++)
                levels[b] = Math.Clamp(MathF.Sqrt(levels[b] / _peak), 0, 1);
        }

        public void Dispose() => Stop();
    }
}
