using Server_V1.Models;

namespace Server_V1.Repos
{
    public class SubcategoriesRepo : GenericRepo<SubCategory>
    {
        public SubcategoriesRepo(ApplicationDbContext dbContext) : base(dbContext)
        {
        }
   
    }
}
