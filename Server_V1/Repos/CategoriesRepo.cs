using Server_V1.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Server_V1.Repos
{
    public class CategoriesRepo : GenericRepo<Category>
    {
        public CategoriesRepo(ApplicationDbContext dbContext) : base(dbContext)
        {
        }


    }
}
