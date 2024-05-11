using System;
using System.Linq;
using System.Windows.Forms;

namespace XrmToolBox.AutoUpdater
{
    internal static class Program
    {
        /// <summary>
        /// Point d'entrée principal de l'application.
        /// </summary>
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new AutoUpdater(args.FirstOrDefault()));
        }
    }
}