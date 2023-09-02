// PROJECT : XrmToolBox
// This project was developed by Tanguy Touzard
// CODEPLEX: http://xrmtoolbox.codeplex.com
// BLOG: http://mscrmtools.blogspot.com

using System;
using System.ServiceModel;
using JetBrains.Annotations;

namespace XrmToolBox
{
    public static class CrmExceptionHelper
    {
        [UsedImplicitly]
        public static string GetErrorMessage(Exception error, bool returnWithStackTrace)
        {
            if (error.InnerException is FaultException)
            {
                return returnWithStackTrace ? (error.InnerException).ToString() : (error.InnerException).Message;
            }
            else if (error.InnerException != null)
            {
                return returnWithStackTrace ? error.InnerException.ToString() : error.InnerException.Message;
            }
            else
            {
                return returnWithStackTrace ? error.ToString() : error.Message;
            }
        }
    }
}