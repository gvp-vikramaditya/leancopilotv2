import argparse
import lean_cli.commands as commands

def main(argv=None):
    parser = argparse.ArgumentParser(description="Lean CLI")
    subparsers = parser.add_subparsers(dest="command")

    run_parser = subparsers.add_parser("run", help="Run a project")
    run_parser.add_argument("project", help="Project name")

    build_parser = subparsers.add_parser("build", help="Build a project")
    build_parser.add_argument("project", help="Project name")

    test_parser = subparsers.add_parser("test", help="Test a project")
    test_parser.add_argument("project", help="Project name")

    args = parser.parse_args(argv)
    if args.command == "run":
        commands.run(args.project)
    elif args.command == "build":
        commands.build(args.project)
    elif args.command == "test":
        commands.test(args.project)

if __name__ == "__main__":
    main()
