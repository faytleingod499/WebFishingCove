# Rebuilds package/WolfparkHUD/WolfparkHUD.pck from source/ with the Godot 3.5 engine's own packer.
# Run from this folder with:  godot --no-window -s pack.gd
extends SceneTree

func _init():
	var out = OS.get_environment("PCK_OUT")
	if out == "":
		out = "package/WolfparkHUD/WolfparkHUD.pck"
	var source = OS.get_environment("PCK_SRC")
	if source == "":
		source = "source/mods/WolfparkHUD/main.gd"
	var packer = PCKPacker.new()
	packer.pck_start(out, 32)
	packer.add_file("res://mods/WolfparkHUD/main.gd", source)
	# the Lonely title as a resource file, for registering it through Lure when Lure is installed
	var title_file = source.get_base_dir().plus_file("titles/lonely.tres")
	if File.new().file_exists(title_file):
		packer.add_file("res://mods/WolfparkHUD/titles/lonely.tres", title_file)
	var result = packer.flush(true)
	print("pack result: ", result)
	quit()
