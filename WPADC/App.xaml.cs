using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WPADC.Services;
using WPADC.Services.Interfaces;
using WPADC.Services.Vision;
using WPADC.ViewModels;

namespace WPADC
{
    /// <summary>
    /// 应用程序入口类，配置DI容器并管理应用程序生命周期
    /// </summary>
    /// <remarks>
    /// DI容器配置说明：
    /// - 所有服务通过接口注册为Singleton（单例），确保全局唯一实例
    /// - MainViewModel通过DI容器获取所有服务依赖（构造函数注入）
    /// - 切换实现只需修改注册代码（如将MotionSimulatorService替换为MotionRealService）
    /// 
    /// 依赖注入的三种生命周期：
    /// - Singleton: 全局唯一实例，适合有状态的服务（如运动控制、相机）
    /// - Transient: 每次请求创建新实例，适合无状态的轻量服务
    /// - Scoped: 每个作用域内唯一实例（Web场景常用，WPF中较少使用）
    /// </remarks>
    public partial class App : Application
    {
        /// <summary>
        /// 全局DI服务提供者，用于解析服务依赖
        /// </summary>
        public static IServiceProvider Services { get; private set; } = null!;

        /// <summary>
        /// 应用程序启动时配置DI容器
        /// </summary>
        /// <remarks>
        /// 注册顺序说明：
        /// 1. 基础服务（日志、配置）最先注册
        /// 2. 视觉服务随后注册
        /// 3. 运动服务最后注册
        /// 4. MainViewModel 最后注册
        /// 
        /// 注意：DI容器会自动解析构造函数参数的依赖顺序，
        /// 注册顺序不影响运行结果，但按依赖关系排列更易阅读。
        /// </remarks>
        protected override void OnStartup(StartupEventArgs e)
        {
            // 在任何 Halcon 类型被加载/调用之前配置运行时环境（HALCONROOT / HALCONLICENSE / PATH）
            HalconRuntimeBootstrap.EnsureInitialized();

            base.OnStartup(e);

            // 配置DI服务集合
            var services = new ServiceCollection();

            // === 通用服务 ===
            services.AddSingleton<ILoggerService, LoggerService>();
            services.AddSingleton<IConfigService, ConfigService>();

            // === 视觉服务 ===
            services.AddSingleton<ICameraService, CameraService>();
            services.AddSingleton<ICalibrationService, CalibrationService>();
            services.AddSingleton<IDistortionCorrectionService, DistortionCorrectionService>();
            services.AddSingleton<ITemplateManagerService, TemplateManagerService>();
            services.AddSingleton<IMarkDetectionService, MarkDetectionService>();
            services.AddSingleton<ICoordinateTransformService, CoordinateTransformService>();

            // === 运动服务 ===
            // 【当前使用仿真模式】如需切换到真实硬件，将下面一行改为：
            // services.AddSingleton<IMotionService, MotionRealService>();
            services.AddSingleton<IMotionService, MotionSimulatorService>();

            // === 视图模型 ===
            services.AddSingleton<MainViewModel>();

            // 构建DI容器
            Services = services.BuildServiceProvider();

            // 创建主窗口，通过DI容器获取MainViewModel
            var mainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>()
            };
            mainWindow.Show();
        }

        /// <summary>
        /// 应用程序退出时释放资源
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            // 释放DI容器中实现了IDisposable的服务
            if (Services is IDisposable disposable)
            {
                disposable.Dispose();
            }
            base.OnExit(e);
        }
    }
}
