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

        private StreamReader reader;

        private StreamWriter writer;

        public FileManager(string filePath) 
        {
            this.filePath = filePath;
        }

        public string readOneLine()
        {
            try
            {
                if(this.reader == null)
                {
                    string currentDirectory = Directory.GetCurrentDirectory();
                    this.reader = new StreamReader(@Path.Combine(currentDirectory, this.filePath));
                }
                string firstLine = this.reader.ReadLine();
                this.reader.Close();
                return firstLine;
            }
            catch (FileNotFoundException e)
            {
                if(this.reader != null)
                {
                    this.reader.Close();
                }
                System.Diagnostics.Debug.WriteLine(String.Format("===== Error in reading file: {0}. {1}",
                    this.filePath, e.Message));
                return null;
            }
        }

        public void writeLine(string line) 
        {
            if (this.reader != null) 
            {
                throw(new Exception("Reader of the same file name already exists."));
            }
            if (this.writer == null)
            {
                string currentDirectory = Directory.GetCurrentDirectory();
                this.writer = new StreamWriter(@Path.Combine(currentDirectory, this.filePath));
            }
            this.writer.WriteLine(line);
        }

        public void writerClose() 
        {
            if (this.writer != null)
            {
                this.writer.Close();
            }
        }
    }
}
