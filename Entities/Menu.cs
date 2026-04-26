using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace BearingFaultDiagnosis.Entities
{
    public class Menu
    {
        /// <summary>
        /// 菜单主键 ID。
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// 菜单名称。
        /// </summary>
        [Required]
        [MaxLength(100)]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 导航路由（页面路径）。
        /// </summary>
        [Required]
        [MaxLength(200)]
        public string Route { get; set; } = string.Empty;

        /// <summary>
        /// 菜单图标标识。
        /// </summary>
        [MaxLength(100)]
        public string? Icon { get; set; }

        /// <summary>
        /// 父级菜单 ID。
        /// </summary>
        public long? ParentId { get; set; }

        /// <summary>
        /// 显示排序值。
        /// </summary>
        public int SortOrder { get; set; }

        /// <summary>
        /// 菜单是否可见。
        /// </summary>
        public bool IsVisible { get; set; } = true;

        /// <summary>
        /// 父级菜单导航属性。
        /// </summary>
        public Menu? Parent { get; set; }

        /// <summary>
        /// 子级菜单集合。
        /// </summary>
        public ICollection<Menu> Children { get; set; } = new List<Menu>();

        /// <summary>
        /// 菜单与角色关联集合。
        /// </summary>
        public ICollection<RoleMenu> RoleMenus { get; set; } = new List<RoleMenu>();
    }
}
