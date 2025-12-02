using ModelContextProtocol.Server;
using Server_V1.Models;
using Server_V1.Repos;
using System.ComponentModel;

namespace Server_V1.Tools
{
    internal class CategoryTools
    {
        private readonly CategoriesRepo _categoriesRepo;

        public CategoryTools(CategoriesRepo categoriesRepo)
        {
            _categoriesRepo = categoriesRepo;
        }

        [McpServerTool(Name = "vision_apps_categories")]
        [Description("Retrieves and returns the full list of Vision Apps categories from the categories repository for MCP clients.")]
        public List<Category> GetAllCategories()
        {
            return _categoriesRepo.GetAll().ToList();
        }

        [McpServerTool(Name = "get_category_by_id")]
        [Description("Retrieves and returns a single category by id from the categories repository for MCP clients.")]
        public Category GetCategory(
           [Description("id of category to be retreived")] int categoryID)
        {
            return _categoriesRepo.GetById(categoryID);
        }
    }
}
