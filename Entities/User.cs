using System;
using System.ComponentModel.DataAnnotations;

namespace BearingFaultDiagnosis.Entities
{
    public class User
    {
        /// <summary>
        /// 用户主键 ID。
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// 登录用户名。
        /// </summary>
        [Required]
        [MaxLength(50)]
        public string UserName { get; set; } = string.Empty;

        /// <summary>
        /// 用户密码哈希值。
        /// </summary>
        [Required]
        [MaxLength(256)]
        public string PasswordHash { get; set; } = string.Empty;

        /// <summary>
        /// 用户显示名称。
        /// </summary>
        [MaxLength(100)]
        public string? DisplayName { get; set; }

        /// <summary>
        /// 用户是否启用。
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// 用户创建时间（UTC）。
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 关联角色 ID。
        /// </summary>
        public long? RoleId { get; set; }

        /// <summary>
        /// 关联角色导航属性。
        /// </summary>
        public Role? Role { get; set; }
    }
}
