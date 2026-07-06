using System;
using System.IO;
using System.Collections.Generic;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;

namespace EmbyUserControl
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public override string Name => "Emby User Control";
        public override string Description => "控制指定用户每天的播放时长。";

        private Guid _id = new Guid("A8B8808D-79CD-49D6-B06A-EC0542385C66");
        public override Guid Id => _id;

        public static Plugin Instance { get; private set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;

            // 调试用：提取嵌入资源到物理路径，验证打包是否完整
            try
            {
                var assembly = GetType().Assembly;
                var resourceName = GetType().Namespace + ".Configuration.configPage.html";
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
                        {
                            var htmlContent = reader.ReadToEnd();
                            var outputPath = Path.Combine(applicationPaths.PluginConfigurationsPath, "debug_configPage_extracted.html");
                            File.WriteAllText(outputPath, htmlContent, System.Text.Encoding.UTF8);
                            
                            // 同时输出到项目目录供快速查看
                            File.WriteAllText("/Users/jianxiong.cao/work/fun/emby-user-control/debug_configPage_extracted.html", htmlContent, System.Text.Encoding.UTF8);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 忽略异常，防崩
            }
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "embyusercontrol",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
                },
                new PluginPageInfo
                {
                    Name = "embyusercontroljs",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js"
                }
            };
        }

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".thumb.png");
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;
    }
}
