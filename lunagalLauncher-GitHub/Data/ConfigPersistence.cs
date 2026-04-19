using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace lunagalLauncher.Data
{
    /// <summary>
    /// 配置持久化管理器
    /// Manages configuration persistence (loading and saving)
    /// </summary>
    public class ConfigPersistence
    {
        /// <summary>
        /// 配置文件路径
        /// Path to the configuration file
        /// </summary>
        private readonly string _configFilePath;

        /// <summary>
        /// JSON 序列化设置
        /// JSON serialization settings for pretty formatting
        /// </summary>
        private readonly JsonSerializerSettings _jsonSettings;

        /// <summary>
        /// 构造函数
        /// Constructor - initializes the configuration file path
        /// </summary>
        public ConfigPersistence()
        {
            // 获取应用数据目录路径：%APPDATA%\lunagalLauncher
            // Get application data directory path: %APPDATA%\lunagalLauncher
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appFolder = Path.Combine(appDataPath, "lunagalLauncher");

            // 确保目录存在
            // Ensure directory exists
            if (!Directory.Exists(appFolder))
            {
                Log.Information("创建配置目录: {AppFolder}", appFolder);
                Directory.CreateDirectory(appFolder);
            }

            // 设置配置文件完整路径
            // Set full configuration file path
            _configFilePath = Path.Combine(appFolder, "config.json");

            // 配置 JSON 序列化设置（格式化输出）
            // Configure JSON serialization settings (formatted output)
            _jsonSettings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore
            };

            Log.Information("配置文件路径: {ConfigFilePath}", _configFilePath);
        }

        /// <summary>
        /// 供热重载等场景使用的配置文件完整路径
        /// </summary>
        public string ConfigFilePath => _configFilePath;

        /// <summary>
        /// 加载配置文件
        /// Loads configuration from file
        /// </summary>
        /// <returns>配置对象，如果文件不存在则返回默认配置 / Configuration object, or default if file doesn't exist</returns>
        public RootConfig LoadConfig()
        {
            try
            {
                // 检查配置文件是否存在
                // Check if configuration file exists
                if (!File.Exists(_configFilePath))
                {
                    Log.Warning("配置文件不存在，创建默认配置: {ConfigFilePath}", _configFilePath);

                    // 创建默认配置
                    // Create default configuration
                    var defaultConfig = CreateDefaultConfig();

                    // 保存默认配置到文件
                    // Save default configuration to file
                    SaveConfig(defaultConfig);

                    return defaultConfig;
                }

                // 读取配置文件内容
                // Read configuration file content
                Log.Information("正在加载配置文件: {ConfigFilePath}", _configFilePath);
                string jsonContent = File.ReadAllText(_configFilePath);

                // 反序列化 JSON 为配置对象
                // Deserialize JSON to configuration object
                var config = JsonConvert.DeserializeObject<RootConfig>(jsonContent, _jsonSettings);

                if (config == null)
                {
                    Log.Error("配置文件反序列化失败，返回默认配置");
                    return CreateDefaultConfig();
                }

                MouseMappingMigration.EnsureRulesFromLegacy(config);
                MouseMappingMigration.MigrateSpatialDisablesToGlobal(config.MouseMapping);

                Log.Information("配置文件加载成功");
                return config;
            }
            catch (JsonException jsonEx)
            {
                // JSON 解析错误
                // JSON parsing error
                Log.Error(jsonEx, "配置文件 JSON 解析失败: {Message}", jsonEx.Message);
                return CreateDefaultConfig();
            }
            catch (IOException ioEx)
            {
                // 文件读取错误
                // File reading error
                Log.Error(ioEx, "配置文件读取失败: {Message}", ioEx.Message);
                return CreateDefaultConfig();
            }
            catch (Exception ex)
            {
                // 其他未知错误
                // Other unknown errors
                Log.Error(ex, "加载配置文件时发生未知错误: {Message}", ex.Message);
                return CreateDefaultConfig();
            }
        }

        /// <summary>
        /// 保存配置到文件
        /// Saves configuration to file
        /// </summary>
        /// <param name="config">要保存的配置对象 / Configuration object to save</param>
        /// <returns>是否保存成功 / Whether save was successful</returns>
        /// <summary>
        /// 序列化完整 <see cref="RootConfig"/>（与 config.json 相同格式），用于「导出设置」备份文件。
        /// </summary>
        public string SerializeFullSettingsForExport(RootConfig config)
        {
            return JsonConvert.SerializeObject(config, _jsonSettings);
        }

        /// <summary>
        /// 从备份 JSON 解析完整配置；须包含 LaunchSettings 节点，避免将「仅鼠标映射」文件误当全量导入而清空应用列表。
        /// </summary>
        public bool TryParseFullSettingsImport(string json, out RootConfig? config)
        {
            config = null;
            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                var trimmed = json.TrimStart();
                if (!trimmed.StartsWith("{"))
                    return false;

                var jo = JObject.Parse(json);
                var ls = jo["LaunchSettings"] ?? jo["launchSettings"];
                if (ls == null || ls.Type == JTokenType.Null)
                {
                    Log.Warning("全量导入：JSON 缺少 LaunchSettings，已拒绝（请使用本程序导出的完整备份）");
                    return false;
                }

                var c = JsonConvert.DeserializeObject<RootConfig>(json, _jsonSettings);
                if (c == null)
                    return false;

                NormalizeImportedRootConfig(c);
                config = c;
                return true;
            }
            catch (JsonException ex)
            {
                Log.Error(ex, "全量设置导入 JSON 解析失败: {Message}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 补齐 null 集合/子对象并执行鼠标映射迁移，保证新环境导入后可立即使用。
        /// </summary>
        private static void NormalizeImportedRootConfig(RootConfig c)
        {
            c.Version ??= "1.0";

            c.LaunchSettings ??= new LaunchSettings();
            c.LaunchSettings.Apps ??= new List<AppConfig>();
            c.LaunchSettings.LaunchMode = string.IsNullOrWhiteSpace(c.LaunchSettings.LaunchMode)
                ? "normal"
                : c.LaunchSettings.LaunchMode;

            var llama = c.LaunchSettings.LlamaService ??= new LlamaServiceConfig();
            llama.ModelPathHistory ??= new List<string>();
            llama.HostHistory ??= new List<string>();
            llama.ModelSearchPaths ??= new List<string>();
            llama.Presets ??= new List<LlamaServicePreset>();

            c.MouseMapping ??= new MouseMappingConfig();
            c.MouseMapping.Rules ??= new List<MouseMappingRule>();
            c.MouseMapping.GlobalProcessFilter ??= new List<string>();
            c.MouseMapping.ExportPathHistory ??= new List<string>();
            c.MouseMapping.ImportPathHistory ??= new List<string>();
            c.MouseMapping.LeftClick ??= new LeftClickConfig();
            c.MouseMapping.RightClick ??= new RightClickConfig();
            c.MouseMapping.MiddleClick ??= new MiddleClickConfig();

            c.WindowSettings ??= new WindowSettings();

            MouseMappingMigration.EnsureRulesFromLegacy(c);
            MouseMappingMigration.MigrateSpatialDisablesToGlobal(c.MouseMapping);
        }

        public bool SaveConfig(RootConfig config)
        {
            try
            {
                Log.Information("正在保存配置文件: {ConfigFilePath}", _configFilePath);

                // 序列化配置对象为 JSON
                // Serialize configuration object to JSON
                string jsonContent = JsonConvert.SerializeObject(config, _jsonSettings);

                // 写入文件
                // Write to file
                File.WriteAllText(_configFilePath, jsonContent);

                Log.Information("配置文件保存成功");
                return true;
            }
            catch (JsonException jsonEx)
            {
                // JSON 序列化错误
                // JSON serialization error
                Log.Error(jsonEx, "配置对象序列化失败: {Message}", jsonEx.Message);
                return false;
            }
            catch (IOException ioEx)
            {
                // 文件写入错误
                // File writing error
                Log.Error(ioEx, "配置文件写入失败: {Message}", ioEx.Message);
                return false;
            }
            catch (UnauthorizedAccessException authEx)
            {
                // 权限不足错误
                // Unauthorized access error
                Log.Error(authEx, "没有权限写入配置文件: {Message}", authEx.Message);
                return false;
            }
            catch (Exception ex)
            {
                // 其他未知错误
                // Other unknown errors
                Log.Error(ex, "保存配置文件时发生未知错误: {Message}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 创建默认配置
        /// Creates default configuration
        /// </summary>
        /// <returns>默认配置对象 / Default configuration object</returns>
        private RootConfig CreateDefaultConfig()
        {
            Log.Information("创建默认配置");

            // 创建默认配置对象
            // Create default configuration object
            var config = new RootConfig
            {
                Version = "1.0",
                LaunchSettings = new LaunchSettings
                {
                    AutoLaunchOnStartup = false,
                    LaunchMode = "normal",
                    CloseAppsOnExit = false,
                    Apps = new System.Collections.Generic.List<AppConfig>(),
                    LlamaService = new LlamaServiceConfig
                    {
                        Enabled = true,
                        IsBuiltIn = true
                    }
                },
                MouseMapping = new MouseMappingConfig
                {
                    LeftClick = new LeftClickConfig
                    {
                        Enabled = true,
                        ShortPressThreshold = 200,
                        AutoClickInterval = 100
                    },
                    RightClick = new RightClickConfig
                    {
                        Enabled = true,
                        LongPressThreshold = 500
                    },
                    MiddleClick = new MiddleClickConfig
                    {
                        Enabled = true,
                        LongPressThreshold = 500,
                        CustomHotkey = "Shift+Z"
                    }
                }
            };

            MouseMappingMigration.EnsureRulesFromLegacy(config);

            return config;
        }

    }
}
