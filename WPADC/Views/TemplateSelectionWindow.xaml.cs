using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace WPADC.Views
{
    /// <summary>
    /// 模板项数据模型，表示一个可选择的视觉匹配模板
    /// </summary>
    public class TemplateItem
    {
        /// <summary>模板名称（目录名）</summary>
        public string Name { get; set; }
        /// <summary>模板目录完整路径</summary>
        public string FullPath { get; set; }
    }

    /// <summary>
    /// 模板选择窗口，用于加载或删除已保存的视觉匹配模板
    /// </summary>
    public partial class TemplateSelectionWindow : Window
    {
        /// <summary>选中模板的完整路径</summary>
        public string SelectedTemplatePath { get; private set; }
        /// <summary>被删除模板的名称</summary>
        public string DeletedTemplateName { get; private set; }
        /// <summary>是否请求删除操作</summary>
        public bool DeleteRequested { get; private set; }

        /// <summary>模板列表数据源</summary>
        private readonly ObservableCollection<TemplateItem> _items = new();

        /// <summary>
        /// 模板选择窗口构造函数，扫描模板目录并填充列表
        /// </summary>
        /// <param name="templatesDir">模板根目录路径</param>
        public TemplateSelectionWindow(string templatesDir)
        {
            InitializeComponent();

            if (Directory.Exists(templatesDir))
            {
                // 扫描模板目录，仅包含MarkModel1.shm文件的目录才视为有效模板
                var dirs = Directory.GetDirectories(templatesDir);
                foreach (var d in dirs)
                {
                    if (File.Exists(Path.Combine(d, "MarkModel1.shm")))
                    {
                        _items.Add(new TemplateItem
                        {
                            Name = Path.GetFileName(d),
                            FullPath = d
                        });
                    }
                }
            }

            // 绑定数据源并默认选中第一项
            TemplateList.ItemsSource = _items;
            if (_items.Count > 0)
                TemplateList.SelectedIndex = 0;
        }

        /// <summary>
        /// 加载按钮点击事件，确认选择当前模板并关闭窗口
        /// </summary>
        private void Load_Click(object sender, RoutedEventArgs e)
        {
            if (TemplateList.SelectedItem is TemplateItem item)
            {
                SelectedTemplatePath = item.FullPath;
                DialogResult = true;
            }
            else
            {
                MessageBox.Show("请先选择一个模板", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// 删除按钮点击事件，确认删除当前选中的模板（不可恢复）
        /// </summary>
        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (TemplateList.SelectedItem is TemplateItem item)
            {
                var result = MessageBox.Show($"确定删除模板 '{item.Name}' 吗？此操作不可恢复。",
                    "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    DeletedTemplateName = item.Name;
                    SelectedTemplatePath = item.FullPath;
                    DeleteRequested = true;
                    DialogResult = true;
                }
            }
            else
            {
                MessageBox.Show("请先选择一个模板", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// 取消按钮点击事件，放弃选择并关闭窗口
        /// </summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        /// <summary>
        /// 模板列表双击事件，双击选中项直接加载模板
        /// </summary>
        private void TemplateList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (TemplateList.SelectedItem is TemplateItem item)
            {
                SelectedTemplatePath = item.FullPath;
                DialogResult = true;
            }
        }
    }
}
