# Rebuilds package/Playground/Playground.pck from source/ with the Godot 3.5 engine's own packer.
# Run from this folder with:  godot --no-window -s pack.gd
extends SceneTree

const FILES = ["main.gd", "swing.gd", "swing.scn"]

func _init():
	var out = OS.get_environment("PCK_OUT")
	if out == "":
		out = "package/Playground/Playground.pck"
	var src = OS.get_environment("PCK_SRC_DIR")
	if src == "":
		src = "source/mods/Playground"
	var packer = PCKPacker.new()
	packer.pck_start(out, 32)
	for name in FILES:
		packer.add_file("res://mods/Playground/" + name, src.plus_file(name))
	print("pack result: ", packer.flush(true))
	quit()
