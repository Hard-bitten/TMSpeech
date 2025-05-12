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

        private List<SpeakerSegment> _speakerSegments = new List<SpeakerSegment>();
        private OfflineSpeakerDiarization _speakerDiarization;
        private bool _meetingModeEnabled = false;

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
                                // 安全地获取音频数据副本
                                List<float> audioDataCopy = null;
                                lock (_bufferLock)
                                {
                                    // 如果写入缓冲区为空，则不需要处理
                                    if (_writeBuffer.Count == 0)
                                    {
                                        continue;
                                    }
                                    
                                    // 创建写入缓冲区的完整副本，而不是交换缓冲区
                                    audioDataCopy = new List<float>(_writeBuffer);
                                    
                                    // 清空写入缓冲区，为新数据做准备
                                    _writeBuffer.Clear();
                                }
                                
                                // 确保我们有数据可处理
                                if (audioDataCopy == null || audioDataCopy.Count == 0)
                                {
                                    continue;
                                }
                                
                                // 使用不带回调的Process方法，避免回调函数导致的内存问题
                                Trace.TraceInformation("开始处理说话者识别，音频数据长度: {0}", audioDataCopy.Count);
                                
                                // 将List转换为数组
                                
                                // 处理音频数据，获取说话者分割结果
                                var segmentSize = 60 * 1600; // 60秒的音频数据

                                    // 将audioDataCopy 按每60秒分割
                                    var segCnt = audioDataCopy.Count() / segmentSize;
                                    var audioDataSegments = new List<float[]>();
                                    for (int i = 0; i < segCnt; i++)
                                    {
                                        var segment = audioDataCopy.Skip(i * segmentSize).Take(segmentSize).ToArray();
                                        float[] audioData = segment.ToArray();

                                        // 分为两段进行执行
                                        OfflineSpeakerDiarizationSegment[] segments = null;

                                        try
                                        {
                                            // 使用固定大小的数组并确保GC不会在处理过程中移动它
                                            GCHandle handle = GCHandle.Alloc(audioData, GCHandleType.Pinned);
                                            try
                                            {
                                                // 获取固定数组的指针
                                                IntPtr ptr = handle.AddrOfPinnedObject();

                                                // 使用Process方法处理音频数据
                                                segments = _speakerDiarization.Process(audioData);
                                            }
                                            finally
                                            {
                                                // 确保释放固定的内存
                                                if (handle.IsAllocated)
                                                    handle.Free();
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Trace.TraceError("{0:HH:mm:ss.fff} Error processing audio data: {1}", DateTime.Now, ex);
                                            continue;
                                        }

                                        Trace.TraceInformation("说话者识别处理完成，分割结果数量: {0}", segments?.Count() ?? 0);

                                        // 不需要清空读取缓冲区，因为我们使用的是副本

                                        // 如果有分割结果，添加说话者信息
                                        if (segments != null && segments.Any())
                                        {
                                            // 找到时长最长的说话者
                                            var longestSegment = segments.OrderByDescending(s => s.End - s.Start).FirstOrDefault();
                                            if (longestSegment != null)
                                            {
                                                // 添加说话者信息到文本
                                                item = new TextInfo($"[说话者_{longestSegment.Speaker}] {text}");
                                            }

                                        }
                                    }
                                    
                                
                                
                                
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
            }
            _speakerSegments.Clear();
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