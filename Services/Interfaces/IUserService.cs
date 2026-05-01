using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BearingFaultDiagnosis.Entities;

namespace BearingFaultDiagnosis.Services.Interfaces
{
    public interface IUserService
    {
        User Login(string username, string password);
        List<Menu> GetMenusByRoleId(long? roleId);
    }
}
