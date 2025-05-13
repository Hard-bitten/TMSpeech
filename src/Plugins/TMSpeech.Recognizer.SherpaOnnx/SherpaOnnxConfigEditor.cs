using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TMSpeech.Core.Plugins;

namespace TMSpeech.Recognizer.SherpaOnnx
{
    class SherpaOnnxConfig
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("encoder")]
        public string Encoder { get; set; } = "models\\encoder.onnx";

        [JsonPropertyName("decoder")]
        public string Decoder { get; set; } = "models\\decoder.onnx";

        [JsonPropertyName("joiner")]
        public string Joiner { get; set; } = "models\\joiner.onnx";

        [JsonPropertyName("tokens")]
        public string Tokens { get; set; } = "models\\tokens.txt";

        [JsonPropertyName("enable_punctuation")]
        public bool EnablePunctuation { get; set; } = true;

        [JsonPropertyName("punctuation_model")]
        public string PunctuationModel { get; set; } = "models\\punctuation.onnx";

        [JsonPropertyName("enable_meeting_mode")]
        public bool EnableMeetingMode { get; set; } = false;

        [JsonPropertyName("segmentation_model")]
        public string SegmentationModel { get; set; } = "models\\segmentation.onnx";

        [JsonPropertyName("embedding_model")]
        public string EmbeddingModel { get; set; } = "models\\speaker-embedding.onnx";

        [JsonPropertyName("num_clusters")]
        public int NumClusters { get; set; } = 5;

        [JsonPropertyName("use_clustering_threshold")]
        public bool UseClusteringThreshold { get; set; } = false;

        [JsonPropertyName("clustering_threshold")]
        public double ClusteringThreshold { get; set; } = 0.5f;
    }

    public class SherpaOnnxConfigEditor : IPluginConfigEditor
    {
        private SherpaOnnxConfig _config = new SherpaOnnxConfig();

        public void SetValue(string key, object value)
        {
            if (key == "model")
            {
                _config.Model = (string)value;
                FormItemsUpdated?.Invoke(this, EventArgs.Empty);
            }

            if (key == "encoder") _config.Encoder = (string)value;
            if (key == "decoder") _config.Decoder = (string)value;
            if (key == "joiner") _config.Joiner = (string)value;
            if (key == "tokens") _config.Tokens = (string)value;
            if (key == "enable_punctuation") _config.EnablePunctuation = (bool)value;
            if (key == "punctuation_model") _config.PunctuationModel = (string)value;
            if (key == "enable_meeting_mode")
            {
                _config.EnableMeetingMode = (bool)value;
                // 当会议模式状态改变时，更新表单项
                FormItemsUpdated?.Invoke(this, EventArgs.Empty);
            }
            if (key == "segmentation_model") _config.SegmentationModel = (string)value;
            if (key == "embedding_model") _config.EmbeddingModel = (string)value;
            if (key == "num_clusters") _config.NumClusters = Convert.ToInt32(value);
            if (key == "use_clustering_threshold") _config.UseClusteringThreshold = (bool)value;
            if (key == "clustering_threshold") _config.ClusteringThreshold = Convert.ToDouble(value);
        }

        public object GetValue(string key)
        {
            if (key == "model") return _config.Model;
            if (key == "encoder") return _config.Encoder;
            if (key == "decoder") return _config.Decoder;
            if (key == "joiner") return _config.Joiner;
            if (key == "tokens") return _config.Tokens;
            if (key == "enable_punctuation") return _config.EnablePunctuation;
            if (key == "punctuation_model") return _config.PunctuationModel;
            if (key == "enable_meeting_mode") return _config.EnableMeetingMode;
            if (key == "segmentation_model") return _config.SegmentationModel;
            if (key == "embedding_model") return _config.EmbeddingModel;
            if (key == "num_clusters") return _config.NumClusters;
            if (key == "use_clustering_threshold") return _config.UseClusteringThreshold;
            if (key == "clustering_threshold") return _config.ClusteringThreshold;
            return "";
        }

        public IReadOnlyList<PluginConfigFormItem> GetFormItems()
        {
            var models = Core.Services.Resource.ResourceManagerFactory.Instance.GetLocalModuleInfos().Result
                .Where(u => u.Type == Core.Services.Resource.ModuleInfoTypeEnums.SherpaOnnxModel);

            var options = new Dictionary<object, string>
            {
                { "", "自定义模型" },
            }.Union(
                models.Select(x => new KeyValuePair<object, string>(x.ID,
                    $"{x.Name} ({x.ID})"))
            ).ToDictionary(x => x.Key, x => x.Value);

            if (!string.IsNullOrEmpty(_config.Model))
            {
                var modelFormItems = new List<PluginConfigFormItem>
                {
                    new PluginConfigFormItemOption("model", "模型", options),
                    new PluginConfigFormCheckBox("enable_punctuation", "启用标点符号"),
                    new PluginConfigFormItemFile("punctuation_model", "标点符号模型"),
                    new PluginConfigFormCheckBox("enable_meeting_mode", "启用会议模式（说话者识别）")
                };

                // 只有在启用会议模式时才显示相关配置项
                if (_config.EnableMeetingMode)
                {
                    modelFormItems.AddRange(new PluginConfigFormItem[]
                    {
                        new PluginConfigFormItemFile("segmentation_model", "说话者分割模型"),
                        new PluginConfigFormItemFile("embedding_model", "说话者嵌入模型"),
                        new PluginConfigFormItemNumber("num_clusters", "说话者数量", "", 1, 10, true),
                        new PluginConfigFormCheckBox("use_clustering_threshold", "使用聚类阈值"),
                        new PluginConfigFormItemNumber("clustering_threshold", "聚类阈值", "", 1, 10, false)
                    });
                }

                return modelFormItems;
            }

            var customFormItems = new List<PluginConfigFormItem>
            {
                new PluginConfigFormItemOption("model", "模型", options),
                new PluginConfigFormItemFile("encoder", "编码器"),
                new PluginConfigFormItemFile("decoder", "解码器"),
                new PluginConfigFormItemFile("joiner", "连接器"),
                new PluginConfigFormItemFile("tokens", "词表"),
                new PluginConfigFormCheckBox("enable_punctuation", "启用标点符号"),
                new PluginConfigFormItemFile("punctuation_model", "标点符号模型"),
                new PluginConfigFormCheckBox("enable_meeting_mode", "启用会议模式（说话者识别）")
            };

            // 只有在启用会议模式时才显示相关配置项
            if (_config.EnableMeetingMode)
            {
                customFormItems.AddRange(new PluginConfigFormItem[]
                {
                    new PluginConfigFormItemFile("segmentation_model", "说话者分割模型"),
                    new PluginConfigFormItemFile("embedding_model", "说话者嵌入模型"),
                    new PluginConfigFormItemNumber("num_clusters", "说话者数量", "", 1, 10, true),
                    new PluginConfigFormCheckBox("use_clustering_threshold", "使用聚类阈值"),
                    new PluginConfigFormItemNumber("clustering_threshold", "聚类阈值", "", 1, 10, false)
                });
            }

            return customFormItems;
        }

        public event EventHandler<EventArgs>? FormItemsUpdated;

        public event EventHandler<EventArgs>? ValueUpdated;

        IReadOnlyDictionary<string, object> IPluginConfigEditor.GetAll()
        {
            return new Dictionary<string, object>()
            {
                { "model", _config.Model },
                { "encoder", _config.Encoder },
                { "decoder", _config.Decoder },
                { "joiner", _config.Joiner },
                { "tokens", _config.Tokens },
                { "enable_punctuation", _config.EnablePunctuation },
                { "punctuation_model", _config.PunctuationModel },
                { "enable_meeting_mode", _config.EnableMeetingMode },
                { "segmentation_model", _config.SegmentationModel },
                { "embedding_model", _config.EmbeddingModel },
                { "num_clusters", _config.NumClusters },
                { "use_clustering_threshold", _config.UseClusteringThreshold },
                { "clustering_threshold", _config.ClusteringThreshold }
            };
        }

        public void LoadConfigString(string data)
        {
            try
            {
                _config = JsonSerializer.Deserialize<SherpaOnnxConfig>(data);
            }
            catch
            {
                _config = new SherpaOnnxConfig();
            }
        }

        public string GenerateConfig()
        {
            return JsonSerializer.Serialize(_config, new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = true
            });
        }
    }
}