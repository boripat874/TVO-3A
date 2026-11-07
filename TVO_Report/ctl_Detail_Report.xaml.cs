using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace TVO_Report
{
    /// <summary>
    /// Interaction logic for ctl_Detail_Report.xaml
    /// </summary>
    public partial class ctl_Detail_Report : UserControl
    {
        public string siloName {  get; set; }
        public List<SensorRow> Sensors { get; set; }
        public ctl_Detail_Report()
        {
            InitializeComponent();
        }

        public void LoadData(string silo, List<SensorRow> pList)
        {
            siloName = silo;
            Sensors = pList;

            txtSilo.Text = silo;

            dgSensor.ItemsSource = pList;
        }
    }
}
