using System;
using System.Collections.Generic;
using System.Text;

namespace Server_V1.Models
{
    public class Category
    {
        public int ID { get; set; }
        public string Name { get; set; } = "not set";
        public string Description { get; set; } = "not set";

        override public string ToString()
        {
            return $"{Name} (ID: {ID}) - {Description}";
        }

    }
}
