using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace grapher
{
    partial class AboutBox : Form
    {
        public AboutBox(Version driver)
        {
            InitializeComponent();
            this.ClientSize = new Size(540, 250);
            this.logoPictureBox.Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap();
            this.Controls.Add(new Label {
                Text = "Based on Raw Accel (MIT). Original authors: simon, _m00se_, Sidiouth, TauntyArmordillo, and Kovaak. Copyright (c) 2020 a1xd. See LICENSE and ReadMe.md.",
                Dock = DockStyle.Bottom, Height = 65, Padding = new Padding(10)
            });
            this.Text = String.Format("About {0}", AssemblyTitle);
            this.labelVersion.Text = String.Format("GUI Version {0}", AssemblyVersion);
            this.labelDriverVersion.Text = String.Format("Driver Version {0}", driver.ToString());
        }

        #region Assembly Attribute Accessors

        public string AssemblyTitle
        {
            get
            {
                object[] attributes = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyTitleAttribute), false);
                if (attributes.Length > 0)
                {
                    AssemblyTitleAttribute titleAttribute = (AssemblyTitleAttribute)attributes[0];
                    if (titleAttribute.Title != "")
                    {
                        return titleAttribute.Title;
                    }
                }
                return System.IO.Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().CodeBase);
            }
        }

        public string AssemblyVersion
        {
            get
            {
                return Assembly.GetExecutingAssembly().GetName().Version.ToString();
            }
        }

        #endregion
    }
}
