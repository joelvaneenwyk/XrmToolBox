﻿using System;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;

namespace XrmToolBox.Extensibility
{
    public class Worker
    {
        private Panel _infoPanel;

        private BackgroundWorker _worker;

        public void CancelWorker()
        {
            if (_worker != null && _worker.WorkerSupportsCancellation)
            {
                _worker.CancelAsync();

                if (_infoPanel != null)
                {
                    _infoPanel.Parent.Controls.Remove(_infoPanel);
                    _infoPanel.Dispose();
                }
            }
        }

        public void SetWorkingMessage(Control host, string message, int width = 340, int height = 150)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            if (host.Controls.Contains(_infoPanel))
            {
                host.Invoke(new Action(() =>
                {
                    _infoPanel.Width = width;
                    _infoPanel.Height = height;
                    var lbl = _infoPanel.Controls.OfType<Label>().FirstOrDefault();
                    lbl.Text = message;
                }));
            }
            else
            {
                _infoPanel = InformationPanel.GetInformationPanel(host, message, width, height);
                host.Controls.Add(_infoPanel);
            }
        }

        public void WorkAsync(WorkAsyncInfo info)
        {
            if (info.Host == null)
            {
                throw new NullReferenceException("WorkAsyncInfo Host property is null!");
            }

            if (!string.IsNullOrEmpty(info.Message))
            {
                _infoPanel = InformationPanel.GetInformationPanel(info.Host, info.Message, info.MessageWidth,
                    info.MessageHeight);
            }
            _worker = new BackgroundWorker
            {
                WorkerReportsProgress = info.ProgressChanged != null,
                WorkerSupportsCancellation = info.IsCancelable
            };
            _worker.DoWork += info.PerformWork;

            if (_worker.WorkerReportsProgress && info.ProgressChanged != null)
            {
                _worker.ProgressChanged += info.PerformProgressChange;
            }

            _worker.RunWorkerCompleted += (s, e) =>
            {
                if (_infoPanel != null)
                {
                    _infoPanel.SendToBack();

                    if (info?.Host?.Controls.Contains(_infoPanel) ?? false)
                    {
                        info.Host.Controls.Remove(_infoPanel);
                    }

                    if (_infoPanel.Tag is Control c && (c?.Controls.Contains(_infoPanel) ?? false))
                    {
                        c.Controls.Remove(_infoPanel);
                    }

                    _infoPanel.Dispose();
                }

                if (info.PostWorkCallBack != null)
                {
                    info.PostWorkCallBack(e);
                }
            };

            _worker.RunWorkerAsync(info.AsyncArgument);
        }
    }
}