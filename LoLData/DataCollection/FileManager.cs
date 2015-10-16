using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LoLData.DataCollection
{
    public class FileManager
    {

        private string filePath;

        public FileManager(string filePath) 
        {
            this.filePath = filePath;
        }

        public string readOneLine()
        {
            try
            {
                string currentDirectory = Directory.GetCurrentDirectory();
                StreamReader file = new StreamReader(@Path.Combine(currentDirectory, this.filePath));
                string firstLine = file.ReadLine();
                file.Close();
                return firstLine;
            }
            catch (FileNotFoundException e)
            {
                System.Diagnostics.Debug.WriteLine(String.Format("===== Error in reading file: {0}. {1}",
                    this.filePath, e.Message));
                return null;
            }
        }
    }
}
