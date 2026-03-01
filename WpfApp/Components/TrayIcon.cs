using Driver;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using WpfApp.Profile;

namespace WpfApp.Components;

public sealed class ProfileItemToolStripItem : ToolStripMenuItem
{
    private readonly ProfileManager profileManager;
    public readonly ProfileItem profileItem;
    public readonly bool isSelected;

    public ProfileItemToolStripItem(ProfileManager profileManager, ProfileItem profileItem, bool isSelected)
    {
        this.profileManager = profileManager;
        this.profileItem = profileItem;
        this.isSelected = isSelected;
        Text = profileItem.Name;
        if (isSelected )
        {
            Image = ProjectResources.deer_1f98c.ToBitmap();
        }
        DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
    }

    protected override void OnClick(EventArgs e)
    {
        profileManager.SwitchTo(profileItem);
        base.OnClick(e);
    }
}

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon icon;
    private readonly ProfileManager profileManager;
    public Action DoubleClick = () => { };
    public Action AppShouldClose = () => { };

    public TrayIcon(ProfileManager profileManager)
    {
        this.profileManager = profileManager;
        this.profileManager.CurrentProfileChanged += ProfileChanged;
        this.profileManager.ProfileCollectionChanged += ProfileCollectionChanged;
        icon = new()
        {
            Icon = ProjectResources.deer_1f98c,
            Visible = true
        };
        icon.BalloonTipClosed += (sender, e) =>
        {
            var thisIcon = sender as NotifyIcon;
            if (thisIcon is { } icon)
            {
                icon.Visible = false;
                icon.Dispose();
            }
        };
        icon.DoubleClick += new EventHandler(TrayIconOnClick);
        icon.ContextMenuStrip = CreateContextMenu();
    }

    private ContextMenuStrip CreateContextMenu()
    {
        ContextMenuStrip menu = new();
        
        var label = new ToolStripLabel("Select a profile")
        {
            Margin = new Padding(0, 3, 0, 3),
        };
        menu.Items.Add(label);
        menu.Items.Add(new ToolStripSeparator());
        var items = profileManager.Profiles.Select(profile => new ProfileItemToolStripItem(profileManager, profile, profileManager.IsSelected(profile))).ToArray();
        menu.Items.AddRange(items);
        menu.Items.Add(new ToolStripSeparator());
        var exit = new ToolStripMenuItem() { Text = "Exit", DisplayStyle = ToolStripItemDisplayStyle.Text };
        exit.Click += (sender, e) => AppShouldClose.Invoke();
        menu.Items.Add(exit);
        return menu;
    }

    void TrayIconOnClick(object? sender, EventArgs e)
    {
        DoubleClick.Invoke();
    }

    private void ProfileCollectionChanged(ProfileItem[] _)
    {
        icon.ContextMenuStrip = CreateContextMenu();
    }

    private void ProfileChanged(int index, ProfileItem item)
    {
        icon.Text = string.Format("Current Profile: {0}", item.Name);
        icon.ContextMenuStrip = CreateContextMenu();
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
