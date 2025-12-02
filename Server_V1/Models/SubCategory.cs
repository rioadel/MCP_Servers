using System;
using System.Collections.Generic;
using System.Text;

namespace Server_V1.Models
{
    public class SubCategory
    {
        public int ID { get; set; }
        public string Name { get; set; } = "not set";

        public int CategoryID { get; set; }

        public Category Category { get; set; } = new Category();
    }
}
