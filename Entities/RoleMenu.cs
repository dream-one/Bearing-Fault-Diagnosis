using System;
using System.ComponentModel.DataAnnotations;

namespace BearingFaultDiagnosis.Entities
{
    public class RoleMenu
    {
        /// <summary>
        /// 角色 ID。
        /// </summary>
        [Required]
        public long RoleId { get; set; }

        /// <summary>
        /// 菜单 ID。
        /// </summary>
        [Required]
        public long MenuId { get; set; }

        /// <summary>
        /// 权限授予时间（UTC）。
        /// </summary>
        public DateTime GrantedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 关联角色导航属性。
        /// </summary>
        public Role Role { get; set; } = null!;

        /// <summary>
        /// 关联菜单导航属性。
        /// </summary>
        public Menu Menu { get; set; } = null!;
    }
}
