using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace jsdoom
{
    class Program
    {
        static void Main(string[] args)
        {
            using (var game = new MainWindow())
                game.Run(60, 60);
        }
    }
}
