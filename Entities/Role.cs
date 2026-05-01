using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace BearingFaultDiagnosis.Entities
{
    public class Role
    {
        /// <summary>
        /// 角色主键 ID。
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// 角色名称。
        /// </summary>
        [Required]
        [MaxLength(50)]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 角色编码（唯一业务标识）。
        /// </summary>
        [Required]
        [MaxLength(50)]
        public string Code { get; set; } = string.Empty;

        /// <summary>
        /// 角色描述。
        /// </summary>
        [MaxLength(200)]
        public string? Description { get; set; }

        /// <summary>
        /// 角色是否启用。
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// 角色下的用户集合。
        /// </summary>
        public ICollection<User> Users { get; set; } = new List<User>();

        /// <summary>
        /// 角色与菜单关联集合。
        /// </summary>
        public ICollection<RoleMenu> RoleMenus { get; set; } = new List<RoleMenu>();
    }
}
