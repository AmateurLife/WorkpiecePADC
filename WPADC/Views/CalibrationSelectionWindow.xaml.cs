using System.Collections.Generic;
using System.Windows;

namespace WPADC.Views
{
    /// <summary>
    /// 标定数据选择窗口，用于从已保存的标定数据列表中选择加载或删除
    /// </summary>
    public partial class CalibrationSelectionWindow : Window
    {
        /// <summary>选中项的索引，-1表示未选中</summary>
        public int SelectedIndex { get; private set; } = -1;
        /// <summary>是否请求删除操作</summary>
        public bool DeleteRequested { get; private set; }

        /// <summary>
        /// 标定数据选择窗口构造函数，填充标定数据名称列表
        /// </summary>
        /// <param name="names">标定数据名称列表</param>
        /// <param name="dataList">标定数据对象列表</param>
        public CalibrationSelectionWindow(List<string> names, List<CalibrationSaveData> dataList)
        {
            InitializeComponent();

            // 填充列表项，格式：序号. 名称
            for (int i = 0; i < names.Count; i++)
            {
                var item = new System.Windows.Controls.ListBoxItem();
                item.Content = $"{i + 1}. {names[i]}";
                CalibList.Items.Add(item);
            }

            // 默认选中第一项
            if (CalibList.Items.Count > 0)
                CalibList.SelectedIndex = 0;
        }

        /// <summary>
        /// 加载按钮点击事件，确认选择当前标定数据并关闭窗口
        /// </summary>
        private void Load_Click(object sender, RoutedEventArgs e)
        {
            if (CalibList.SelectedIndex >= 0)
            {
                SelectedIndex = CalibList.SelectedIndex;
                DeleteRequested = false;
                DialogResult = true;
            }
            else
            {
                MessageBox.Show("请先选择一项", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// 删除按钮点击事件，确认删除当前选中的标定数据
        /// </summary>
        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (CalibList.SelectedIndex >= 0)
            {
                var result = MessageBox.Show("确定删除该标定数据吗？", "确认删除",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    SelectedIndex = CalibList.SelectedIndex;
                    DeleteRequested = true;
                    DialogResult = true;
                }
            }
            else
            {
                MessageBox.Show("请先选择一项", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
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
        /// 列表双击事件，双击选中项直接加载标定数据
        /// </summary>
        private void CalibList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (CalibList.SelectedIndex >= 0)
            {
                SelectedIndex = CalibList.SelectedIndex;
                DeleteRequested = false;
                DialogResult = true;
            }
        }
    }
}
