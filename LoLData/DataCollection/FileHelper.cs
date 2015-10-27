using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LoLData.DataCollection
{
    public class FileHelper
    {
        public static void WriterLogError(StreamWriter writer, string errorString)
        {
            writer.WriteLine("****************");
            writer.WriteLine("Error encountered: ");
            writer.WriteLine(errorString);
            writer.WriteLine("****************");
        }
    }
}
