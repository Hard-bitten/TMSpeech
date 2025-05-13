﻿using SherpaOnnx;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TMSpeech.Core.Plugins;
using TMSpeech.Core.Services.Resource;

namespace TMSpeech.Recognizer.SherpaOnnx
{
    class SherpaOnnxRecognizer : IRecognizer
    {
        public string GUID => "3002EE6C-9770-419F-A745-E3148747AF4C";
        
        public string Name => "Sherpa-Onnx离线识别器";

        public string Description => "一款占用资源少，识别速度快的离线识别器";

        public string Version => "0.0.1";

        public string SupportVersion => "any";

        public string Author => "Built-in";

        public string Url => "";

        public string License => "MIT License";

        public string Note => "";
        public IPluginConfigEditor CreateConfigEditor() => new SherpaOnnxConfigEditor();

        private SherpaOnnxConfig _userConfig = new SherpaOnnxConfig();

        public void LoadConfig(string config)
        {
            if (config != null && config.Length != 0)
            {
                _userConfig = JsonSerializer.Deserialize<SherpaOnnxConfig>(config);
            }
        }

        public bool Available => true;

        public event EventHandler<SpeechEventArgs> TextChanged;
        public event EventHandler<SpeechEventArgs> SentenceDone;

        // 用于存储说话者识别结果
        private class SpeakerSegment
        {
            public float Start { get; set; }
            public float End { get; set; }
            public int Speaker { get; set; }
            public string Text { get; set; }
        }

        // 用于跟踪说话者的历史记录
        private class SpeakerHistory
        {
            public int SpeakerId { get; set; }
            public DateTime LastDetectedTime { get; set; }
            public int ConsecutiveDetections { get; set; }
            public float Confidence { get; set; }
        }

        private List<SpeakerSegment> _speakerSegments = new List<SpeakerSegment>();
        private OfflineSpeakerDiarization _speakerDiarization;
        private bool _meetingModeEnabled = false;
        
        // 音频缓冲区，用于保存较长时间的音频数据
        private List<float> _audioBuffer = new List<float>();
        // 音频缓冲区的最大长度（以采样点为单位，16kHz采样率下60秒约为960000个采样点）
        private const int MAX_AUDIO_BUFFER_SIZE = 480000;
        // 处理窗口大小（以采样点为单位，30秒）
        private const int PROCESSING_WINDOW_SIZE = 160000;
        // 当前活跃的说话者ID
        private int _currentSpeakerId = -1;
        // 上次处理的音频长度（用于确保连续性）
        private int _lastProcessedAudioLength = 0;

        public void Feed(byte[] data)
        {
            var buffer = MemoryMarshal.Cast<byte, float>(data);
            var floatArray = buffer.ToArray();
            
            // 如果启用了会议模式，需要保存音频数据用于后续的说话者识别
            if (_meetingModeEnabled && _userConfig.EnableMeetingMode)
            {
                lock (_bufferLock)
                {
                    _writeBuffer.AddRange(floatArray.Select(f => f));
                    
                    // 同时更新音频缓冲区
                    _audioBuffer.AddRange(floatArray);
                    // 如果音频缓冲区超过最大长度，移除最早的数据
                    if (_audioBuffer.Count > MAX_AUDIO_BUFFER_SIZE)
                    {
                        _audioBuffer.RemoveRange(0, _audioBuffer.Count - MAX_AUDIO_BUFFER_SIZE);
                    }
                }
            }
            stream?.AcceptWaveform(config.FeatConfig.SampleRate, floatArray);
        }

        private OnlineRecognizer recognizer;

        private OnlineStream stream;

        private bool stop = false;

        private Thread thread;

        private OnlineRecognizerConfig config;

        private OfflinePunctuation punctuation;
        
        // 单缓冲区实现，但使用副本进行处理
        private readonly object _bufferLock = new object();
        private List<float> _writeBuffer = new List<float>();

        private void Run()
        {
            config = new OnlineRecognizerConfig();
            config.FeatConfig.SampleRate = 16000;
            config.FeatConfig.FeatureDim = 80;

            string encoder, decoder, joiner, tokens;
            
            // 初始化会议模式（说话者识别）
            if (_userConfig.EnableMeetingMode)
            {
                try
                {
                    _meetingModeEnabled = true;
                    var sdConfig = new OfflineSpeakerDiarizationConfig();
                    
                    // 设置说话者分割模型
                    sdConfig.Segmentation.Pyannote.Model = _userConfig.SegmentationModel;
                    
                    // 设置说话者嵌入模型
                    sdConfig.Embedding.Model = _userConfig.EmbeddingModel;
                    
                    // 设置聚类参数
                    if (_userConfig.UseClusteringThreshold)
                    {
                        sdConfig.Clustering.Threshold = (float)_userConfig.ClusteringThreshold;
                    }
                    else
                    {
                        sdConfig.Clustering.NumClusters = _userConfig.NumClusters;
                    }
                    
                    _speakerDiarization = new OfflineSpeakerDiarization(sdConfig);
                    lock (_bufferLock)
                    {
                        _writeBuffer = new List<float>();
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceError("{0:HH:mm:ss.fff} Failed to initialize speaker diarization: {1}", DateTime.Now, ex);
                    _meetingModeEnabled = false;
                }
            }
            
            // 初始化标点符号功能
            if (_userConfig.EnablePunctuation && !string.IsNullOrEmpty(_userConfig.PunctuationModel))
            {
                try
                {
                    var punctConfig = new OfflinePunctuationConfig();
                    punctConfig.Model.CtTransformer = _userConfig.PunctuationModel;
                    punctConfig.Model.Debug = 1;
                    punctConfig.Model.NumThreads = 1;
                    punctuation = new OfflinePunctuation(punctConfig);
                }
                catch (Exception ex)
                {
                    Trace.TraceError("{0:HH:mm:ss.fff} Failed to initialize punctuation: {1}", DateTime.Now, ex);
                    punctuation = null;
                }
            }

            if (!string.IsNullOrEmpty(_userConfig.Model))
            {
                var res = ResourceManagerFactory.Instance.GetLocalResource(_userConfig.Model).Result;
                if (res == null) throw new InvalidDataException("Cannot find model: " + _userConfig.Model);
                encoder = Path.Combine(res.LocalDir, res.ModuleInfo.SherpaOnnxModelPath.EncoderPath);
                decoder = Path.Combine(res.LocalDir, res.ModuleInfo.SherpaOnnxModelPath.DecoderPath);
                joiner = Path.Combine(res.LocalDir, res.ModuleInfo.SherpaOnnxModelPath.JoinerPath);
                tokens = Path.Combine(res.LocalDir, res.ModuleInfo.SherpaOnnxModelPath.TokenPath);
            }
            else
            {
                encoder = _userConfig.Encoder;
                decoder = _userConfig.Decoder;
                joiner = _userConfig.Joiner;
                tokens = _userConfig.Tokens;
            }

            foreach (string path in new[] { encoder, decoder, joiner, tokens })
            {
                if (!File.Exists(path))
                {
                    throw new InvalidOperationException("Cannot find model file: " + path +
                                                        "\n Current working directory: " +
                                                        Directory.GetCurrentDirectory());
                }
            }

            config.ModelConfig.Transducer.Encoder = encoder;
            config.ModelConfig.Transducer.Decoder = decoder;
            config.ModelConfig.Transducer.Joiner = joiner;
            config.ModelConfig.Tokens = tokens;
            config.ModelConfig.NumThreads = 1;
            config.ModelConfig.Debug = 1;
            config.DecodingMethod = "greedy_search";
            config.EnableEndpoint = 1;
            config.Rule1MinTrailingSilence = 2.4f;
            config.Rule2MinTrailingSilence = 1.2f;
            config.Rule3MinUtteranceLength = 20;

            recognizer = new OnlineRecognizer(config);
            stream = recognizer.CreateStream();

            while (!stop)
            {
                while (recognizer.IsReady(stream))
                {
                    recognizer.Decode(stream);
                }

                var is_endpoint = recognizer.IsEndpoint(stream);
                var text = recognizer.GetResult(stream).Text;

                if (!string.IsNullOrEmpty(text))
                {
                    // 添加标点符号
                    if (_userConfig.EnablePunctuation && punctuation != null)
                    {
                        text = punctuation.AddPunct(text);
                    }

                    var item = new TextInfo(text);
                    // Console.WriteLine($"{is_endpoint}: {text}");
                    TextChanged?.Invoke(this, new SpeechEventArgs()
                    {
                        Text = item,
                    });

                    if (is_endpoint || text.Length > 80)
                    {
                        // 如果启用了会议模式，处理说话者识别
                        if (_meetingModeEnabled && _userConfig.EnableMeetingMode)
                        {
                            try
                            {
                                // 安全地获取音频缓冲区的副本
                                float[] processingAudio = null;
                                int currentAudioLength = 0;
                                
                                lock (_bufferLock)
                                {
                                    // 如果写入缓冲区为空，则不需要处理
                                    if (_writeBuffer.Count == 0)
                                    {
                                        continue;
                                    }
                                    
                                    // 记录当前新数据的长度
                                    currentAudioLength = _writeBuffer.Count;
                                    
                                    // 将新数据添加到音频缓冲区
                                    _audioBuffer.AddRange(_writeBuffer);
                                    
                                    // 如果音频缓冲区超过最大长度，移除最早的数据
                                    if (_audioBuffer.Count > MAX_AUDIO_BUFFER_SIZE)
                                    {
                                        _audioBuffer.RemoveRange(0, _audioBuffer.Count - MAX_AUDIO_BUFFER_SIZE);
                                    }
                                    
                                    // 提取最近的30秒音频数据用于处理
                                    // 确保包含当前分片和相邻分片，以保证信息连续性
                                    int dataToProcess = Math.Min(PROCESSING_WINDOW_SIZE, _audioBuffer.Count);
                                    processingAudio = new float[dataToProcess];
                                    Array.Copy(_audioBuffer.ToArray(), _audioBuffer.Count - dataToProcess, processingAudio, 0, dataToProcess);
                                    
                                    // 清空写入缓冲区，为新数据做准备
                                    _writeBuffer.Clear();
                                }
                                
                                // 确保我们有足够的数据可处理
                                if (processingAudio == null || processingAudio.Length < 16000) // 至少1秒的数据
                                {
                                    continue;
                                }
                                
                                Trace.TraceInformation("开始处理说话者识别，处理窗口长度: {0}，当前新数据长度: {1}",
                                    processingAudio.Length, currentAudioLength);
                                
                                // 处理提取的音频数据，获取说话者分割结果
                                OfflineSpeakerDiarizationSegment[] segments = _speakerDiarization.Process(processingAudio);
                                
                                Trace.TraceInformation("说话者识别处理完成，分割结果数量: {0}", segments?.Count() ?? 0);
                                
                                // 如果有分割结果，分析说话者信息
                                if (segments != null && segments.Any())
                                {
                                    // 计算每个说话者的总发言时间
                                    Dictionary<int, float> speakerDurations = new Dictionary<int, float>();
                                    
                                    // 计算当前新数据在处理窗口中的起始时间点
                                    float currentDataStartTime = 0;
                                    if (processingAudio.Length > currentAudioLength)
                                    {
                                        currentDataStartTime = (float)(processingAudio.Length - currentAudioLength) / config.FeatConfig.SampleRate;
                                    }
                                    
                                    // 主要关注当前新数据部分的说话者
                                    // 但也考虑相邻部分的信息以保证连续性
                                    foreach (var segment in segments)
                                    {
                                        // 计算与当前新数据的重叠部分
                                        float overlapStart = Math.Max(segment.Start, currentDataStartTime);
                                        float overlapEnd = segment.End;
                                        
                                        if (overlapEnd > currentDataStartTime) // 有重叠
                                        {
                                            float duration = overlapEnd - overlapStart;
                                            
                                            // 给予当前新数据部分更高的权重
                                            float weight = 1.0f;
                                            if (segment.Start < currentDataStartTime)
                                            {
                                                // 如果段开始于历史数据部分，给予较低权重
                                                weight = 0.8f;
                                            }
                                            
                                            if (speakerDurations.ContainsKey(segment.Speaker))
                                            {
                                                speakerDurations[segment.Speaker] += duration * weight;
                                            }
                                            else
                                            {
                                                speakerDurations[segment.Speaker] = duration * weight;
                                            }
                                        }
                                    }
                                    
                                    // 找出发言时间最长的说话者
                                    int dominantSpeaker = -1;
                                    
                                    if (speakerDurations.Any())
                                    {
                                        dominantSpeaker = speakerDurations
                                            .OrderByDescending(pair => pair.Value)
                                            .First().Key;
                                        
                                        Trace.TraceInformation(
                                            "检测到的主要说话者: {0}, 累计时长: {1:F2}秒",
                                            dominantSpeaker,
                                            speakerDurations[dominantSpeaker]
                                        );
                                    }
                                    
                                    // 确定是否需要切换当前说话者
                                    if (dominantSpeaker >= 0)
                                    {
                                        // 如果当前没有活跃说话者，或者检测到的说话者与当前活跃说话者不同
                                        if (_currentSpeakerId < 0 || dominantSpeaker != _currentSpeakerId)
                                        {
                                            _currentSpeakerId = dominantSpeaker;
                                            Trace.TraceInformation("切换到新说话者: {0}", _currentSpeakerId);
                                        }
                                        
                                        // 添加说话者信息到文本
                                        item = new TextInfo($"[说话者_{_currentSpeakerId}] {text}");
                                        
                                        Trace.TraceInformation("最终使用的说话者ID: {0}", _currentSpeakerId);
                                    }
                                }
                                
                                // 更新上次处理的音频长度
                                _lastProcessedAudioLength = currentAudioLength;
                            }
                            catch (Exception ex)
                            {
                                Trace.TraceError("{0:HH:mm:ss.fff} Speaker diarization error: {1}", DateTime.Now, ex);
                            }
                        }
                        
                        SentenceDone?.Invoke(this, new SpeechEventArgs()
                        {
                            Text = item,
                        });
                        recognizer.Reset(stream);
                    }
                }

                Thread.Sleep(20);
            }
        }

        public void Start()
        {
            if (thread != null)
                throw new InvalidOperationException("The recognizer is already running.");
            stop = false;
            thread = new Thread(() =>
            {
                try
                {
                    Run();
                }
                catch (Exception e)
                {
                    Trace.TraceError("{0:HH:mm:ss.fff} Exception {1}", DateTime.Now, e);
                    ExceptionOccured?.Invoke(this, e);
                }
                finally
                {
                    stop = true;
                    thread = null;
                }
            });
            thread.Start();
        }

        public void Stop()
        {
            stream?.InputFinished();
            stop = true;
            thread?.Join();

            stream?.Dispose();
            recognizer?.Dispose();
            punctuation?.Dispose();
            _speakerDiarization?.Dispose();
            recognizer = null;
            stream = null;
            punctuation = null;
            _speakerDiarization = null;
            lock (_bufferLock)
            {
                _writeBuffer.Clear();
                _audioBuffer.Clear();
            }
            _speakerSegments.Clear();
            _currentSpeakerId = -1;
            _lastProcessedAudioLength = 0;
            thread = null;
        }

        public event EventHandler<Exception>? ExceptionOccured;

        public void Init()
        {
            Debug.WriteLine("SherpaOnnxRecognizer Init");
        }

        public void Destroy()
        {
        }
    }
}