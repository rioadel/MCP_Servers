using Microsoft.EntityFrameworkCore;
using Server_V1.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Server_V1.Repos
{
    public class GenericRepo<T> where T : class
    {
        private readonly ApplicationDbContext _dbContext;

        public GenericRepo(ApplicationDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public IQueryable<T> GetAll()
        {
            // Implementation for retrieving all entities of type T
            return _dbContext.Set<T>();
        }

        public T GetById(int id)
        {
            // Implementation for retrieving a single entity by its ID
            return _dbContext.Set<T>().Find(id)!;
        }

        public void Add(T entity)
        {
            // Implementation for adding a new entity
            _dbContext.Set<T>().Add(entity);
            _dbContext.SaveChanges();
        }

        public void Update(T entity)
        {
            // Implementation for updating an existing entity
            _dbContext.Set<T>().Update(entity);
            _dbContext.SaveChanges();
        }

        public void Delete(int id)
        {
            // Implementation for deleting an entity by its ID
            var entity = _dbContext.Set<T>().Find(id);
            if (entity != null)
            {
                _dbContext.Set<T>().Remove(entity);
                _dbContext.SaveChanges();
            }
        }
    }
}
