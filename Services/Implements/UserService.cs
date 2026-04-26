using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BearingFaultDiagnosis.Core;
using BearingFaultDiagnosis.Entities;
using BearingFaultDiagnosis.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Internal;

namespace BearingFaultDiagnosis.Services.Implements
{
    public class UserService : IUserService
    {
        private IDbContextFactory<AppDbContext> _dbContext;
        public UserService(IDbContextFactory<AppDbContext> dbContextFactory)
        {
            _dbContext = dbContextFactory;
        }
        public User Login(string username, string password)
        {
            using (var context = _dbContext.CreateDbContext())
            {
                var user = context.Users.Where(m => m.UserName == username && m.IsActive).FirstOrDefault();
                if (user == null)
                {
                    return null;
                }
                bool isPasswordCorrect = BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);
                if (isPasswordCorrect)
                {
                    return user;
                }
                return null;
            }
        }

        public List<Menu> GetMenusByRoleId(long? roleId)
        {
            using var context = _dbContext.CreateDbContext();
            return context.RoleMenus
                .Where(r => r.RoleId == roleId)
                .Select(r => r.Menu)
                .ToList();
        }
    }
}
