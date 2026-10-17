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
	var result = packer.flush(true)
	print("pack result: ", result)
	quit()
